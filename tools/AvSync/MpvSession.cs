using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace HandPegApp.Tools.AvSync;

/// <summary>
/// One libmpv player with no window, for the harness: the app's player (Services/MpvPlayer.cs) in everything that
/// decides what is played and when, but writing what it plays into a file (mpv's encoding mode) instead of onto a
/// screen and the speakers. Calls go through the library's addresses, as the app's do, and every request is sent with
/// mpv's asynchronous calls, which its core runs in the order they were sent.
/// </summary>
internal sealed unsafe class MpvSession : IDisposable
{
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _destroy;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, int> _setOption;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, int, void*, int> _setPropertyAsync;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, ulong, byte**, int> _commandAsync;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, int, int> _observe;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, int> _requestLog;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, double, Event*> _waitEvent;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*> _getPropertyString;
    private readonly delegate* unmanaged[Cdecl]<void*, void> _free;
    private readonly delegate* unmanaged[Cdecl]<int, byte*> _errorString;

    private readonly IntPtr _mpv;
    private readonly Thread _events;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private volatile bool _ended, _shutdown;

    // mpv's own numbers, from its client.h.
    private const int EventShutdown = 1, EventLogMessage = 2, EventSetPropertyReply = 4, EventCommandReply = 5, EventEndFile = 7,
        EventFileLoaded = 8, EventPlaybackRestart = 21, EventPropertyChange = 22;
    private const int FormatString = 1, FormatDouble = 5;
    private const ulong WatchTime = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Event
    {
        public int Id;
        public int Error;
        public ulong UserData;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyEvent
    {
        public IntPtr Name;
        public int Format;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LogEvent
    {
        public IntPtr Prefix;
        public IntPtr Level;
        public IntPtr Text;
    }

    private MpvSession(IntPtr library, IReadOnlyList<(string Name, string Value)> options, string logLevel)
    {
        var create = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(library, "mpv_create");
        var initialize = (delegate* unmanaged[Cdecl]<IntPtr, int>)NativeLibrary.GetExport(library, "mpv_initialize");
        _destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(library, "mpv_terminate_destroy");
        _setOption = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, int>)NativeLibrary.GetExport(library, "mpv_set_option_string");
        _setPropertyAsync = (delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, int, void*, int>)NativeLibrary.GetExport(library, "mpv_set_property_async");
        _commandAsync = (delegate* unmanaged[Cdecl]<IntPtr, ulong, byte**, int>)NativeLibrary.GetExport(library, "mpv_command_async");
        _observe = (delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, int, int>)NativeLibrary.GetExport(library, "mpv_observe_property");
        _requestLog = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)NativeLibrary.GetExport(library, "mpv_request_log_messages");
        _waitEvent = (delegate* unmanaged[Cdecl]<IntPtr, double, Event*>)NativeLibrary.GetExport(library, "mpv_wait_event");
        _getPropertyString = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*>)NativeLibrary.GetExport(library, "mpv_get_property_string");
        _free = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(library, "mpv_free");
        _errorString = (delegate* unmanaged[Cdecl]<int, byte*>)NativeLibrary.GetExport(library, "mpv_error_string");

        _mpv = create();
        if (_mpv == IntPtr.Zero)
            throw new InvalidOperationException("libmpv could not be started.");

        foreach (var (name, value) in options)
        {
            fixed (byte* n = Utf8(name), v = Utf8(value))
            {
                var result = _setOption(_mpv, n, v);
                if (result < 0)
                    throw new InvalidOperationException($"libmpv refused the option {name}={value}: {Describe(result)}");
            }
        }

        var started = initialize(_mpv);
        if (started < 0)
            throw new InvalidOperationException($"libmpv could not be started: {Describe(started)}");

        fixed (byte* level = Utf8(logLevel))
            _requestLog(_mpv, level);
        fixed (byte* name = Utf8("time-pos"))
            _observe(_mpv, WatchTime, name, FormatDouble);

        _events = new Thread(ReadEvents) { IsBackground = true, Name = "mpv events" };
        _events.Start();
    }

    /// <summary>Loads libmpv from <paramref name="libmpv"/>, sets the options (before mpv starts, as the app does) and starts it.</summary>
    public static MpvSession Start(string libmpv, IReadOnlyList<(string Name, string Value)> options, string logLevel = "v") =>
        new(NativeLibrary.Load(libmpv), options, logLevel);

    /// <summary>Milliseconds since the session started: what the log and the restarts are stamped with.</summary>
    public long Now => _clock.ElapsedMilliseconds;

    /// <summary>Every line mpv logged, stamped.</summary>
    public List<string> Log { get; } = [];

    /// <summary>The playback position as mpv last reported it, in seconds; NaN before the first report.</summary>
    public double Position { get; private set; } = double.NaN;

    /// <summary>How many times playback has (re)started: after the file opened, and after every seek and new graph.</summary>
    public int Restarts { get; private set; }

    /// <summary>When each restart came, and where it left the position.</summary>
    public List<(long Ms, double Position)> RestartLog { get; } = [];

    /// <summary>The errors mpv answered requests with (a graph it would not take, a seek it could not make).</summary>
    public List<string> Refusals { get; } = [];

    public bool Ended => _ended;

    // ----- Requests (all asynchronous, run by mpv in the order they were sent) -----

    public void Command(params string[] arguments)
    {
        var buffers = arguments.Select(Utf8).ToArray();
        var handles = buffers.Select(b => GCHandle.Alloc(b, GCHandleType.Pinned)).ToArray();
        try
        {
            var starts = stackalloc byte*[arguments.Length + 1];
            for (var i = 0; i < arguments.Length; i++)
                starts[i] = (byte*)handles[i].AddrOfPinnedObject();
            starts[arguments.Length] = null;
            Note($"command {string.Join(' ', arguments.Select(a => a.Length > 120 ? a[..120] + "..." : a))}");
            _commandAsync(_mpv, 0, starts);
        }
        finally
        {
            foreach (var handle in handles)
                handle.Free();
        }
    }

    public void SetProperty(string name, string value)
    {
        fixed (byte* n = Utf8(name), v = Utf8(value))
        {
            var text = v;
            Note($"set {name} ({value.Length} characters)");
            _setPropertyAsync(_mpv, 1, n, FormatString, &text);
        }
    }

    /// <summary>Sends the player to a moment, written as MpvPlayer writes it (to the millisecond).</summary>
    public void Seek(long milliseconds, bool exact) =>
        Command("seek", (milliseconds / 1000.0).ToString("0.###", CultureInfo.InvariantCulture), exact ? "absolute+exact" : "absolute+keyframes");

    /// <summary>A property's value as text, asked for and waited on; null when mpv has none.</summary>
    public string? Get(string name)
    {
        fixed (byte* n = Utf8(name))
        {
            var value = _getPropertyString(_mpv, n);
            if (value == null)
                return null;

            var text = Marshal.PtrToStringUTF8((IntPtr)value);
            _free(value);
            return text;
        }
    }

    // ----- Waiting -----

    /// <summary>Waits until playback has restarted more than <paramref name="count"/> times. Returns the milliseconds waited, or -1.</summary>
    public long WaitRestarts(int count, TimeSpan timeout)
    {
        var started = Now;
        while (Now - started < timeout.TotalMilliseconds)
        {
            lock (_lock)
            {
                if (Restarts > count)
                    return Now - started;
            }

            Thread.Sleep(2);
        }

        return -1;
    }

    /// <summary>Waits until the position satisfies a condition, or the file ended. Returns whether it did.</summary>
    public bool WaitPosition(Func<double, bool> reached, TimeSpan timeout)
    {
        var started = Now;
        while (Now - started < timeout.TotalMilliseconds && !_ended)
        {
            if (!double.IsNaN(Position) && reached(Position))
                return true;
            Thread.Sleep(1);
        }

        return !double.IsNaN(Position) && reached(Position);
    }

    public bool WaitEnded(TimeSpan timeout)
    {
        var started = Now;
        while (Now - started < timeout.TotalMilliseconds && !_ended)
            Thread.Sleep(5);
        return _ended;
    }

    public void Dispose()
    {
        if (_shutdown)
            return;

        Command("quit");
        _events.Join(TimeSpan.FromSeconds(20));
    }

    private void ReadEvents()
    {
        while (true)
        {
            var e = _waitEvent(_mpv, 0.25);
            switch (e->Id)
            {
                case EventShutdown:
                    _shutdown = true;
                    _destroy(_mpv);
                    return;

                case EventPropertyChange when e->UserData == WatchTime && e->Data != IntPtr.Zero:
                    var property = (PropertyEvent*)e->Data;
                    if (property->Format == FormatDouble && property->Data != IntPtr.Zero)
                        Position = *(double*)property->Data;
                    break;

                case EventPlaybackRestart:
                    lock (_lock)
                    {
                        Restarts++;
                        RestartLog.Add((Now, Position));
                    }

                    Note($"PLAYBACK_RESTART at {Position.ToString("0.000", CultureInfo.InvariantCulture)}");
                    break;

                case EventFileLoaded:
                    Note("FILE_LOADED");
                    break;

                case EventEndFile:
                    _ended = true;
                    Note("END_FILE");
                    break;

                case EventSetPropertyReply or EventCommandReply when e->Error < 0:
                    var refusal = $"{(e->Id == EventCommandReply ? "command" : "property")} refused: {Describe(e->Error)}";
                    lock (_lock)
                        Refusals.Add(refusal);
                    Note(refusal);
                    break;

                case EventLogMessage when e->Data != IntPtr.Zero:
                    var log = (LogEvent*)e->Data;
                    Note($"{Marshal.PtrToStringUTF8(log->Level)} {Marshal.PtrToStringUTF8(log->Prefix)}: {Marshal.PtrToStringUTF8(log->Text)?.TrimEnd()}");
                    break;
            }
        }
    }

    /// <summary>Every line is also written to the console as it comes (--echo): for watching a run that seems stuck.</summary>
    public static bool Echo { get; set; }

    private void Note(string line)
    {
        var stamped = $"[{Now,7} ms] {line}";
        lock (Log)
            Log.Add(stamped);
        if (Echo)
            Console.WriteLine(stamped);
    }

    private string Describe(int error) => Marshal.PtrToStringUTF8((IntPtr)_errorString(error)) ?? $"error {error}";

    private static byte[] Utf8(string text)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }
}
