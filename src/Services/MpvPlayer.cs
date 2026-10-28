using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// A small binding to libmpv, HandPeg's video player: only what HandPeg uses of its client API. mpv draws into
/// a window it is given, plays a file, and can run an FFmpeg filter graph (its lavfi-complex property) on the
/// picture as it plays, which is what lets the export's own filters be watched while they are set.
///
/// The library is loaded from the deps folder on first use. Everything here may be called from any thread, and
/// nothing here waits for mpv: every request is queued with mpv's asynchronous calls, which its core runs in the
/// order they were sent (see "Requests"). The events are raised on a thread of this class's own, and it is for
/// the listener to go back to the UI thread.
/// </summary>
public sealed class MpvPlayer
{
    // ----- The library -----

    /// <summary>
    /// libmpv's functions, called through their addresses: no delegate, and no marshalling code made while the
    /// program runs. (Not [LibraryImport]: that needs the library's name when HandPeg is compiled, and libmpv
    /// is loaded from wherever it was downloaded to.) Text goes over as UTF-8 written into room on the stack,
    /// or into a borrowed buffer when it is long, as a filter graph is; mpv copies it before the call returns.
    /// </summary>
    private sealed unsafe class Library(IntPtr handle)
    {
        private readonly delegate* unmanaged[Cdecl]<IntPtr> _create = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(handle, "mpv_create");
        private readonly delegate* unmanaged[Cdecl]<IntPtr, int> _initialize = (delegate* unmanaged[Cdecl]<IntPtr, int>)NativeLibrary.GetExport(handle, "mpv_initialize");
        private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(handle, "mpv_terminate_destroy");
        private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, int> _setOption = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, int>)NativeLibrary.GetExport(handle, "mpv_set_option_string");
        private readonly delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, int, void*, int> _setPropertyAsync = (delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, int, void*, int>)NativeLibrary.GetExport(handle, "mpv_set_property_async");
        private readonly delegate* unmanaged[Cdecl]<IntPtr, ulong, byte**, int> _commandAsync = (delegate* unmanaged[Cdecl]<IntPtr, ulong, byte**, int>)NativeLibrary.GetExport(handle, "mpv_command_async");
        private readonly delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, int, int> _observe = (delegate* unmanaged[Cdecl]<IntPtr, ulong, byte*, int, int>)NativeLibrary.GetExport(handle, "mpv_observe_property");
        private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, int> _requestLog = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)NativeLibrary.GetExport(handle, "mpv_request_log_messages");
        private readonly delegate* unmanaged[Cdecl]<IntPtr, double, Event*> _waitEvent = (delegate* unmanaged[Cdecl]<IntPtr, double, Event*>)NativeLibrary.GetExport(handle, "mpv_wait_event");
        private readonly delegate* unmanaged[Cdecl]<int, byte*> _errorString = (delegate* unmanaged[Cdecl]<int, byte*>)NativeLibrary.GetExport(handle, "mpv_error_string");

        // Longer text than this is written into a borrowed buffer instead of onto the stack.
        private const int OnStack = 512;

        // mpv_format: a property's value given as text.
        private const int FormatString = 1;

        public IntPtr Create() => _create();

        public int Initialize(IntPtr mpv) => _initialize(mpv);

        public void Destroy(IntPtr mpv) => _destroy(mpv);

        public int SetOption(IntPtr mpv, string name, string value)
        {
            byte[]? borrowedName = null, borrowedValue = null;
            try
            {
                fixed (byte* nameText = Encode(name, stackalloc byte[128], ref borrowedName), valueText = Encode(value, stackalloc byte[OnStack], ref borrowedValue))
                    return _setOption(mpv, nameText, valueText);
            }
            finally
            {
                Release(borrowedName);
                Release(borrowedValue);
            }
        }

        /// <summary>Queues a property change; the answer comes back as an event with <paramref name="reply"/> in it.</summary>
        public int SetPropertyAsync(IntPtr mpv, ulong reply, string name, string value)
        {
            byte[]? borrowedName = null, borrowedValue = null;
            try
            {
                fixed (byte* nameText = Encode(name, stackalloc byte[128], ref borrowedName), valueText = Encode(value, stackalloc byte[OnStack], ref borrowedValue))
                {
                    var text = valueText;
                    return _setPropertyAsync(mpv, reply, nameText, FormatString, &text);
                }
            }
            finally
            {
                Release(borrowedName);
                Release(borrowedValue);
            }
        }

        public Event* WaitEvent(IntPtr mpv, double timeout) => _waitEvent(mpv, timeout);

        public string? ErrorString(int error) => Marshal.PtrToStringUTF8((IntPtr)_errorString(error));

        public int Observe(IntPtr mpv, ulong id, string name, int format)
        {
            byte[]? borrowed = null;
            try
            {
                fixed (byte* text = Encode(name, stackalloc byte[OnStack], ref borrowed))
                    return _observe(mpv, id, text, format);
            }
            finally
            {
                Release(borrowed);
            }
        }

        public int RequestLog(IntPtr mpv, string level)
        {
            byte[]? borrowed = null;
            try
            {
                fixed (byte* text = Encode(level, stackalloc byte[OnStack], ref borrowed))
                    return _requestLog(mpv, text);
            }
            finally
            {
                Release(borrowed);
            }
        }

        /// <summary>
        /// Queues a command and its arguments: one run of C strings, and a list of where each starts with a null
        /// at its end. The answer comes back as an event with <paramref name="reply"/> in it.
        /// </summary>
        public int CommandAsync(IntPtr mpv, ulong reply, ReadOnlySpan<string> arguments)
        {
            var most = 0;
            foreach (var argument in arguments)
                most += Encoding.UTF8.GetMaxByteCount(argument.Length) + 1;

            byte[]? borrowed = null;
            Span<byte> room = stackalloc byte[OnStack];
            if (most > OnStack)
                room = borrowed = System.Buffers.ArrayPool<byte>.Shared.Rent(most);

            var starts = stackalloc byte*[arguments.Length + 1];
            try
            {
                fixed (byte* text = room)
                {
                    var at = 0;
                    for (var i = 0; i < arguments.Length; i++)
                    {
                        starts[i] = text + at;
                        at += Encoding.UTF8.GetBytes(arguments[i], room[at..]);
                        room[at++] = 0;
                    }

                    starts[arguments.Length] = null;
                    return _commandAsync(mpv, reply, starts);
                }
            }
            finally
            {
                Release(borrowed);
            }
        }

        /// <summary>The text as a C string, in the room given when it fits there and in a borrowed buffer when it does not.</summary>
        private static Span<byte> Encode(string text, Span<byte> room, ref byte[]? borrowed)
        {
            var most = Encoding.UTF8.GetMaxByteCount(text.Length) + 1;
            if (most > room.Length)
                room = borrowed = System.Buffers.ArrayPool<byte>.Shared.Rent(most);

            var length = Encoding.UTF8.GetBytes(text, room);
            room[length] = 0;
            return room;
        }

        private static void Release(byte[]? borrowed)
        {
            if (borrowed is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(borrowed);
        }
    }

    private static readonly object LoadLock = new();
    private static Library? _library;

    /// <summary>Whether libmpv has been downloaded.</summary>
    public static bool IsInstalled => File.Exists(DependencyUpdater.MpvPath);

    private static Library Load()
    {
        lock (LoadLock)
            return _library ??= new Library(NativeLibrary.Load(DependencyUpdater.MpvPath));
    }

    // mpv's own numbers, from its client.h.
    private const int EventNone = 0, EventShutdown = 1, EventLogMessage = 2, EventSetPropertyReply = 4, EventCommandReply = 5,
        EventEndFile = 7, EventFileLoaded = 8, EventPlaybackRestart = 21, EventPropertyChange = 22;
    private const int FormatFlag = 3, FormatDouble = 5;

    // What each watched property is asked for under: its changes come back with the number, so that no name
    // has to be read (and made into a string) for every frame that is shown.
    private const ulong WatchTime = 1, WatchDuration = 2, WatchPause = 3, WatchEnd = 4;

    // What the answers to requests are told apart by: the seek being waited for, and the filter graph.
    private const ulong ReplyNone = 0, ReplySeek = 100, ReplyGraph = 101;

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

    // ----- One player -----

    private readonly Library _api;
    private readonly IntPtr _mpv;
    private volatile bool _closing;

    // The file that is open, for the times it has to be opened again where it was.
    private string? _path;

    private MpvPlayer(Library api, IntPtr mpv) => (_api, _mpv) = (api, mpv);

    /// <summary>Raised with the playback position in milliseconds as it moves, for every frame: see <see cref="PositionPending"/> for the merged kind.</summary>
    public event Action<long>? TimeChanged;

    /// <summary>
    /// Raised when the position has moved and nobody has taken the last one yet (<see cref="TryTakePosition"/>):
    /// at most once until it is taken, however many frames go by. A listener that reads the position on every
    /// screen refresh while playing need not listen at all.
    /// </summary>
    public event Action? PositionPending;

    /// <summary>Raised with the length of the loaded file in milliseconds.</summary>
    public event Action<long>? DurationChanged;

    /// <summary>Raised when playback starts, pauses, or runs into the end of the file.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Raised when a file has been opened and is ready to play: the moment to give it its filters. Opening a
    /// file always takes off the graph that was running, so it has to be given again each time.
    /// </summary>
    public event Action? FileLoaded;

    /// <summary>Raised with a line mpv logged as an error: a filter graph it could not build, a file it could not open.</summary>
    public event Action<string>? ErrorLogged;

    /// <summary>Raised with mpv's reason when it would not take a filter graph it was given.</summary>
    public event Action<string>? GraphRefused;

    /// <summary>Where playback is, in milliseconds, as last reported.</summary>
    public long PositionMs { get; private set; }

    /// <summary>The last line mpv logged as an error, or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// A file is being opened and is not ready yet. A filter graph given now would leave it stuck before its
    /// first frame; it is given when <see cref="FileLoaded"/> is raised.
    /// </summary>
    public bool IsLoading { get; private set; }

    public bool IsPaused { get; private set; } = true;

    /// <summary>The end of the file has been reached; mpv holds the last frame.</summary>
    public bool IsEnded { get; private set; }

    public bool IsPlaying => !IsPaused && !IsEnded;

    /// <summary>
    /// Creates a player that draws into the given window. Loading the library and starting mpv take a moment,
    /// so this is meant to be called away from the UI thread. Throws when libmpv cannot be loaded or started.
    /// </summary>
    /// <param name="hardwareAcceleration">Whether the graphics card decodes the video. It always draws it.</param>
    public static MpvPlayer Create(IntPtr window, bool hardwareAcceleration)
    {
        Library api;
        try
        {
            api = Load();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or IOException)
        {
            throw new InvalidOperationException($"libmpv could not be loaded: {ex.Message}", ex);
        }

        var mpv = api.Create();
        if (mpv == IntPtr.Zero)
            throw new InvalidOperationException("libmpv could not be started.");

        var player = new MpvPlayer(api, mpv) { _hardwareAcceleration = hardwareAcceleration };

        // A bare player: no configuration files, no on-screen controls, no keys or mouse of its own. The window
        // it draws in is HandPeg's. The picture is drawn by the graphics card through Direct3D 11, and the
        // last frame stays up at the end of the file, as the timeline expects.
        foreach (var (name, value) in new[]
                 {
                     ("wid", window.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)),
                     ("config", "no"), ("terminal", "no"), ("osc", "no"), ("osd-level", "0"),
                     ("input-default-bindings", "no"), ("input-vo-keyboard", "no"), ("input-cursor", "no"), ("cursor-autohide", "no"),
                     ("idle", "yes"), ("force-window", "yes"), ("drag-and-drop", "no"),

                     // While it is being sent from place to place, speed comes before fidelity: frames may be dropped on
                     // the way to an exact position, and the decoder may cut corners. The picture it rests on is whole.
                     ("hr-seek-framedrop", "yes"), ("vd-lavc-fast", "yes"), ("keep-open", "yes"), ("pause", "yes"),

                     // gpu-next: the renderer built on libplacebo, which keeps its compiled shaders between runs.
                     // The older one is named after it, for a libmpv that was built without.
                     ("vo", "gpu-next,gpu"), ("gpu-api", "d3d11"), ("hwdec", player.DecodingMode),
                     ("gpu-shader-cache", "yes"),
                     ("hr-seek", "yes"), ("audio-display", "no"), ("sub-auto", "no"), ("sid", "no"),

                     // The only subtitles shown are HandPeg's own caption preview, drawn exactly as its file says.
                     ("sub-ass-override", "no"), ("sub-visibility", "yes"),

                     // The cheap way of putting the picture on screen: it is a preview, and the filters want the processor.
                     ("profile", "fast"), ("vd-lavc-threads", "0"), ("framedrop", "vo"),
                 })
        {
            api.SetOption(mpv, name, value);
        }

        // A self-contained copy keeps the renderer's compiled shaders in its own folder, not in %LocalAppData%\mpv.
        if (AppPaths.IsSelfContained)
            api.SetOption(mpv, "gpu-shader-cache-dir", Path.Combine(AppPaths.Cache, "mpv"));

        var started = api.Initialize(mpv);
        if (started < 0)
        {
            api.Destroy(mpv);
            throw new InvalidOperationException($"libmpv could not be started: {player.Describe(started)}");
        }

        api.RequestLog(mpv, "error");
        api.Observe(mpv, WatchTime, "time-pos", FormatDouble);
        api.Observe(mpv, WatchDuration, "duration", FormatDouble);
        api.Observe(mpv, WatchPause, "pause", FormatFlag);
        api.Observe(mpv, WatchEnd, "eof-reached", FormatFlag);

        new Thread(player.ReadEvents) { IsBackground = true, Name = "mpv events" }.Start();
        return player;
    }

    /// <summary>Opens a file, at the given position, playing or paused.</summary>
    public void Open(string path, long startMs, bool paused)
    {
        lock (_sendLock)
        {
            // Whatever was waiting to be sent belonged to the file before.
            (_pendingGraph, _pendingSeek, _seekInFlight) = (null, null, false);
            OpenLocked(path, startMs, paused);
        }
    }

    private void OpenLocked(string path, long startMs, bool paused)
    {
        // A file opened while a graph is set does not start playing: the graph goes first, and whoever set
        // it sets it again once the file is open (see FileLoaded).
        if (_hasGraph)
        {
            _hasGraph = false;
            SetProperty("lavfi-complex", "");
        }

        (_graphTakesSound, _seekAgain) = (false, null);
        _path = path;
        IsLoading = true;
        IsEnded = false;
        SetProperty("pause", paused ? "yes" : "no");
        Command(ReplyNone, "loadfile", path, "replace", "-1", $"start={Number(startMs / 1000.0)}");
    }

    public void SetPause(bool paused)
    {
        // HANDPEG_LOG_PLAY=1: how long Play takes to move the picture on (see NotePlayStarted).
        if (LogPlay && !paused && IsPaused)
            (_playAskedAt, _playFromMs, _graphsSincePlay, _firstMoveMs) = (System.Diagnostics.Stopwatch.GetTimestamp(), Volatile.Read(ref _latestPositionMs), 0, -1);
        SetProperty("pause", paused ? "yes" : "no");
    }

    private static readonly bool LogPlay = Environment.GetEnvironmentVariable("HANDPEG_LOG_PLAY") == "1";
    private long _playAskedAt, _playFromMs;
    private int _graphsSincePlay;

    /// <summary>
    /// Logs the time from Play to the first position past where it was paused, and to half a second of the video played
    /// (about 500 ms when nothing held it up), and how many graphs were sent between.
    /// </summary>
    private void NotePlayStarted(long positionMs)
    {
        if (_playAskedAt == 0 || positionMs <= _playFromMs)
            return;

        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(_playAskedAt).TotalMilliseconds;
        if (_firstMoveMs < 0)
            _firstMoveMs = ms;
        if (positionMs < _playFromMs + 500)
            return;

        _playAskedAt = 0;
        AppLog.Write($"Play: the picture moved on {_firstMoveMs:0} ms after Play, and had played 0.5 s of the video {ms:0} ms after it (from {_playFromMs} ms; {_graphsSincePlay} graph(s) sent meanwhile).");
        _firstMoveMs = -1;
    }

    private double _firstMoveMs = -1;

    /// <summary>
    /// Goes to a position: to that very frame, or, much faster, to the I-frame nearest it, which is what
    /// keeps up with a timeline thumb while it is being dragged. Seeks wait their turn (see "Requests").
    /// </summary>
    public void Seek(long positionMs, bool exact) => Request(null, (positionMs, exact));

    // ----- Requests -----
    // Nothing here waits for mpv: the UI thread only queues. A seek and the filter graph go through one slot,
    // and the newest wins: while a seek is under way (from being sent until mpv says playback has restarted),
    // what is asked meanwhile is kept, the newer replacing the older, and sent when it is done. A playhead that
    // is dragged thus asks for as many frames as the player can show, at the pace it can show them, and is never
    // left behind a queue of positions it has already passed.
    //
    // A seek is sent before the graph that belongs to it. mpv runs a new graph from where it is (a refresh
    // seek) unless a seek is already waiting, in which case the graph waits for that seek. The other way round,
    // a graph made for the new moment is first run at the old one: a layer that, at the old moment, has minutes
    // of its file behind it must decode all of them to catch up before the seek is even looked at.

    private readonly object _sendLock = new();
    private string? _pendingGraph;
    private (long Ms, bool Exact)? _pendingSeek;
    private bool _seekInFlight;
    private long _seekSentAt, _seekAskedAt;

    // How long a seek is waited for before what is waiting is sent anyway: mpv says nothing for a seek it did not make.
    private const long SeekPatienceMs = 3000;

    /// <summary>
    /// Gives mpv the filter graph to run on the picture, in FFmpeg's own syntax, from [vid1] to [vo]; an empty
    /// string for none. With a position, the player is sent there first and the graph is run from there. What
    /// mpv says about it comes back through <see cref="GraphRefused"/> and <see cref="ErrorLogged"/>.
    /// </summary>
    /// <param name="mayBeOvertaken">
    /// Asked for by nobody waiting on it (the paused player readying the graph it will play with): whatever is asked
    /// while it is under way is sent at once instead of waiting for it, as a seek the user makes must never wait
    /// behind work done ahead of need.
    /// </param>
    public void SetFilterGraph(string graph, (long Ms, bool Exact)? seek = null, bool mayBeOvertaken = false) => Request(graph, seek, mayBeOvertaken);

    private void Request(string? graph, (long Ms, bool Exact)? seek, bool mayBeOvertaken = false)
    {
        lock (_sendLock)
        {
            if (graph is not null)
                _pendingGraph = graph;
            if (seek is not null)
                (_pendingSeek, _seekAskedAt) = (seek, Environment.TickCount64);
            _pendingOvertakable &= mayBeOvertaken;

            if (!_seekInFlight || _inFlightOvertakable || Environment.TickCount64 - _seekSentAt > SeekPatienceMs)
                SendPendingLocked();
        }
    }

    // Whether what waits (and what is under way) was asked for ahead of need, and may be overtaken (see SetFilterGraph).
    private bool _pendingOvertakable = true, _inFlightOvertakable;

    private void SendPendingLocked()
    {
        var (graph, seek, overtakable) = (_pendingGraph, _pendingSeek, _pendingOvertakable);
        (_pendingGraph, _pendingSeek, _seekInFlight, _pendingOvertakable, _inFlightOvertakable) = (null, null, false, true, false);
        if (_closing || (graph is null && seek is null))
            return;

        if (graph is { Length: 0 })
        {
            if (_hasGraph)
            {
                // A graph takes the video track for itself, and mpv does not hand it back when the graph goes: it
                // carries on without a picture and takes that for the end of the file. Opening the file again,
                // where it was (or was going), is what brings the plain picture back.
                _hasGraph = false;
                if (_path is { } path)
                {
                    OpenLocked(path, seek?.Ms ?? PositionMs, IsPaused || IsEnded);
                    return;
                }
            }

            graph = null;
        }

        // What is on screen while paused is a frame from before the change: the same moment is asked for again,
        // so that it is shown as the filters now make it.
        if (seek is null && graph is not null && IsPaused)
            seek = (PositionMs, true);

        if (seek is { } target)
        {
            IsEnded = false;
            Command(ReplySeek, "seek", Number(target.Ms / 1000.0), target.Exact ? "absolute+exact" : "absolute+keyframes");
            (_seekInFlight, _seekSentAt, _inFlightOvertakable) = (true, Environment.TickCount64, overtakable);
        }

        // A graph that takes the sound tracks the running one left alone makes mpv select them, and its demuxer seeks
        // again for them (a refresh seek): the restart after it lands on the next I-frame, not the frame asked for
        // (4.2D: 58.420 -> 59.417 s while paused, the paused sound settle). Asked once more after that restart, with
        // the tracks now taken, the same exact seek lands on its frame. Only while paused: playing on, it is not seen.
        _seekAgain = null;
        if (graph is not null)
        {
            var tookSound = _graphTakesSound;
            _graphTakesSound = graph.Contains("[ao]", StringComparison.Ordinal);
            if (_graphTakesSound && !tookSound && IsPaused && seek is { Exact: true } again)
                _seekAgain = again;
        }

        if (graph is not null)
        {
            // HANDPEG_LOG_GRAPH=1: each graph is written to the log in full before mpv is given it, so that one the
            // player cannot run (or that takes it down) can be read afterwards.
            if (LogGraphs)
                AppLog.Write($"Live graph ({graph.Length} characters): {graph}");

            _hasGraph = true;
            _graphsSincePlay++;
            SetProperty("lavfi-complex", graph, ReplyGraph);

            // A graph with sound takes the audio tracks; the track has to be given again after it.
            if (graph.Contains("[ao]", StringComparison.Ordinal))
                _audioTrack = null;
        }
    }

    /// <summary>A seek is over (or never happened): what waited for it goes now.</summary>
    private void SeekDone()
    {
        lock (_sendLock)
        {
            _seekInFlight = false;

            // The seek that a graph taking the sound tracks threw off (see SendPendingLocked), unless a newer one waits.
            if (_seekAgain is { } again && _pendingSeek is null && IsPaused && !_closing)
                _pendingSeek = again;
            _seekAgain = null;

            if (_pendingGraph is not null || _pendingSeek is not null)
                SendPendingLocked();
        }
    }

    /// <summary>Takes the filter graph off without opening the file again: for when another file is about to be opened anyway.</summary>
    public void DropFilterGraph()
    {
        lock (_sendLock)
        {
            _pendingGraph = null;
            if (!_hasGraph)
                return;

            (_hasGraph, _graphTakesSound) = (false, false);
            SetProperty("lavfi-complex", "");
        }
    }

    private bool _hasGraph;
    private bool _awaitingFirstFrame;

    // Whether the graph given last takes the sound tracks, and the exact seek to send again once the restart in flight
    // is over (see SendPendingLocked).
    private bool _graphTakesSound;
    private (long Ms, bool Exact)? _seekAgain;

    private static readonly bool LogGraphs = Environment.GetEnvironmentVariable("HANDPEG_LOG_GRAPH") == "1";

    // ----- Where playback is -----
    // mpv reports the position for every frame it shows. It is kept here, and a listener is told only that there
    // is a new one; it takes the newest when it is ready for it. Nothing piles up on the UI thread while it is busy.

    private long _latestPositionMs;
    private int _positionPending;

    /// <summary>Takes the newest position, if it moved since it was last taken.</summary>
    public bool TryTakePosition(out long positionMs)
    {
        positionMs = Volatile.Read(ref _latestPositionMs);
        return Interlocked.Exchange(ref _positionPending, 0) != 0;
    }

    // ----- The graphics card -----
    // Drawing is always the graphics card's. Decoding is too, when that is switched on: the frames then stay
    // on the card from the file to the screen ("auto-safe"). A filter graph works on frames in main memory,
    // so while one is running the card still decodes but hands each frame back ("auto-copy").

    private bool _hardwareAcceleration;
    private bool _filtering;

    private string DecodingMode => !_hardwareAcceleration ? "no" : _filtering ? "auto-copy" : "auto-safe";

    public void SetHardwareAcceleration(bool on)
    {
        if (_hardwareAcceleration == on)
            return;

        _hardwareAcceleration = on;
        SetProperty("hwdec", DecodingMode);

        // The decoder of a file that is already open is not always changed cleanly under it; the file is
        // opened again where it was, which starts it with the new one.
        if (_path is { } path)
            Open(path, PositionMs, IsPaused || IsEnded);
    }

    /// <summary>Says whether a filter graph is about to run (or has stopped), so the frames are decoded to where it needs them.</summary>
    public void SetFiltering(bool filtering)
    {
        if (_filtering == filtering)
            return;

        _filtering = filtering;
        SetProperty("hwdec", DecodingMode);
    }

    /// <param name="volume">0 to 100.</param>
    public void SetVolume(int volume) => SetProperty("volume", Number(volume));

    /// <summary>Silences the player, or lets it be heard again.</summary>
    public void SetMute(bool muted) => SetProperty("mute", muted ? "yes" : "no");

    /// <summary>Lets the file go (+ New Project, 4.1.1-B): nothing is played until the next Open.</summary>
    public void Stop() => Command(ReplyNone, "stop");

    /// <summary>Saves the frame on screen as a picture, as the filters make it but without the subtitles drawn over it.</summary>
    public void SaveFrame(string path) => Command(ReplyNone, "screenshot-to-file", path, "video");

    public void SetSpeed(double speed) => SetProperty("speed", Number(speed));

    /// <summary>
    /// Gives mpv an audio filter chain in its own syntax (its af property); an empty string for none. Used to
    /// silence the stretches of a sequence where the main video is not there.
    /// </summary>
    public void SetAudioFilter(string filter)
    {
        if (filter == _audioFilter)
            return;

        _audioFilter = filter;
        SetProperty("af", filter);
    }

    private string _audioFilter = "";

    // ----- Subtitles -----
    // A subtitle file of HandPeg's own (the captions' preview) is drawn by mpv itself, as a track added to the
    // file that is playing: libass renders it and the graphics card lays it over the picture, on whatever the
    // filter graph made of that picture or on the plain source. Nothing of it goes through the filter graph.

    private readonly object _subtitleLock = new();
    private string _subtitleFile = "";

    /// <summary>
    /// Has mpv draw an ASS file over the picture; an empty string takes it off again. The file is read whole
    /// when it is given, so it may be deleted afterwards. It is kept for the files opened from now on too:
    /// opening a file drops the tracks added to the one before.
    /// </summary>
    public void SetSubtitleFile(string path)
    {
        lock (_subtitleLock)
        {
            if (path == _subtitleFile)
                return;

            var hadOne = _subtitleFile.Length > 0;
            _subtitleFile = path;

            // A file still being opened is given its track when it is ready (see ReadEvents).
            if (IsLoading || _path is null)
                return;

            // The only subtitle track ever selected is this one (sid starts as "no"), so the current one is it.
            if (hadOne)
                Command(ReplyNone, "sub-remove");
            if (path.Length > 0)
                Command(ReplyNone, "sub-add", path, "select");
        }
    }

    /// <summary>
    /// Has mpv play one of the file's audio tracks itself, while no graph mixes the sound. A graph with sound in
    /// it takes the tracks it reads and leaves none selected once it goes, so the track is given again after it.
    /// </summary>
    /// <param name="index">Which of the file's audio tracks, counted from 0; negative for none.</param>
    public void SetAudioTrack(int index)
    {
        var value = index < 0 ? "no" : Number(index + 1);
        lock (_sendLock)
        {
            if (value == _audioTrack)
                return;

            _audioTrack = value;
            SetProperty("aid", value);
        }
    }

    // The track last given, or null once a graph with sound has taken the tracks over.
    private string? _audioTrack;

    /// <summary>
    /// Ends the player. mpv is asked to quit; the thread reading its events then releases it, so nothing here
    /// waits, and the UI thread is never held up by a player that takes its time.
    /// </summary>
    public void Close()
    {
        if (_closing)
            return;

        _closing = true;
        _api.CommandAsync(_mpv, ReplyNone, ["quit"]);
    }

    // Once it has been told to quit, the player is on its way to being released: nothing more is sent to it.
    // (Pausing still is: a player being closed must not go on making sound.)
    private void SetProperty(string name, string value, ulong reply = ReplyNone)
    {
        if (!_closing || name == "pause")
            _api.SetPropertyAsync(_mpv, reply, name, value);
    }

    private void Command(ulong reply, params string[] arguments)
    {
        if (!_closing)
            _api.CommandAsync(_mpv, reply, arguments);
    }

    // Read where mpv keeps them: an event is not copied out, and the position that arrives with every frame
    // costs no memory at all.
    private unsafe void ReadEvents()
    {
        while (true)
        {
            var e = _api.WaitEvent(_mpv, 0.5);
            if (e->Id == EventShutdown)
                break;

            // Once closing, nothing is passed on: whoever was listening has moved to another player.
            if (_closing)
                continue;

            switch (e->Id)
            {
                // Nothing for half a second: a seek that was never answered is given up on, so what waits behind it goes.
                case EventNone:
                    if (_seekInFlight && Environment.TickCount64 - _seekSentAt > SeekPatienceMs)
                        SeekDone();
                    break;

                case EventPropertyChange when e->Data != IntPtr.Zero:
                    var property = (PropertyEvent*)e->Data;
                    if (property->Data == IntPtr.Zero)
                        break;

                    if (property->Format == FormatDouble)
                    {
                        var seconds = *(double*)property->Data;
                        if (e->UserData == WatchTime)
                        {
                            PositionMs = (long)Math.Round(seconds * 1000);
                            Volatile.Write(ref _latestPositionMs, PositionMs);
                            if (LogPlay)
                                NotePlayStarted(PositionMs);
                            if (Interlocked.Exchange(ref _positionPending, 1) == 0)
                                PositionPending?.Invoke();
                            TimeChanged?.Invoke(PositionMs);
                        }
                        else if (e->UserData == WatchDuration)
                            DurationChanged?.Invoke((long)Math.Round(seconds * 1000));
                    }
                    else if (property->Format == FormatFlag)
                    {
                        var flag = *(int*)property->Data != 0;
                        if (e->UserData == WatchPause)
                            IsPaused = flag;
                        else if (e->UserData == WatchEnd)
                            IsEnded = flag;
                        StateChanged?.Invoke();
                    }

                    break;

                // A seek that mpv would not make (no file open, say) is not waited for.
                case EventCommandReply when e->UserData == ReplySeek && e->Error < 0:
                    SeekDone();
                    break;

                case EventSetPropertyReply when e->UserData == ReplyGraph && e->Error < 0:
                    GraphRefused?.Invoke(Describe(e->Error));
                    break;

                // A file counts as open once it has shown its first frame, which mpv reports as playback
                // (re)starting: a graph given between the two is one of the ways to leave it stuck.
                case EventFileLoaded:
                    _awaitingFirstFrame = true;
                    break;

                case EventPlaybackRestart:
                    if (LogPlay && _seekInFlight)
                        AppLog.Write($"Seek: shown {Environment.TickCount64 - _seekSentAt} ms after it was sent, {Environment.TickCount64 - _seekAskedAt} ms after the newest was asked (at {Volatile.Read(ref _latestPositionMs)} ms).");
                    SeekDone();
                    if (!_awaitingFirstFrame)
                        break;

                    _awaitingFirstFrame = false;
                    lock (_subtitleLock)
                    {
                        IsLoading = false;

                        // The new file has none of the tracks that were added to the one before.
                        if (_subtitleFile.Length > 0 && File.Exists(_subtitleFile))
                            Command(ReplyNone, "sub-add", _subtitleFile, "select");
                    }

                    FileLoaded?.Invoke();
                    break;

                case EventEndFile:
                    IsLoading = false;
                    SeekDone();
                    StateChanged?.Invoke();
                    break;

                case EventLogMessage when e->Data != IntPtr.Zero:
                    var log = (LogEvent*)e->Data;
                    var text = $"{Marshal.PtrToStringUTF8(log->Prefix)}: {Marshal.PtrToStringUTF8(log->Text)?.Trim()}";
                    LastError = text;
                    ErrorLogged?.Invoke(text);
                    break;
            }
        }

        _api.Destroy(_mpv);
    }

    private string Describe(int error) => _api.ErrorString(error) ?? $"error {error}";

    private static string Number(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
