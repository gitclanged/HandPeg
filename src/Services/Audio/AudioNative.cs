using System.Runtime.InteropServices;
using HandPegApp.Compositor.Native;

namespace HandPegApp.Services.Audio;

// ----- The sound engine's Windows calls (Pass 5.0-AudioAnd3D, 5.0C) -----
//
// WASAPI (IMMDeviceEnumerator, IMMDevice, IAudioClient / IAudioClient3, IAudioRenderClient, IAudioClock), the endpoint
// notifications, the property store (a device's name), MMCSS, events and timers, and the Media Foundation calls the audio
// decoder needs beyond the compositor's (Compositor/Native/MediaFoundation.cs). COM by hand as the compositor does it
// (Com.Table): each method is a function pointer at its place in the interface's table, IUnknown being 0-2, then each base
// interface's methods in declaration order (Windows.Win32.winmd). No ComImport, no marshaller, nothing allocates.

/// <summary>WAVEFORMATEXTENSIBLE (40 bytes; a plain WAVEFORMATEX is its first 18).</summary>
[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct WaveFormat
{
    [FieldOffset(0)] public ushort FormatTag;
    [FieldOffset(2)] public ushort Channels;
    [FieldOffset(4)] public uint SamplesPerSecond;
    [FieldOffset(8)] public uint AverageBytesPerSecond;
    [FieldOffset(12)] public ushort BlockAlign;
    [FieldOffset(14)] public ushort BitsPerSample;
    [FieldOffset(16)] public ushort ExtraSize;
    [FieldOffset(18)] public ushort ValidBitsPerSample;
    [FieldOffset(20)] public uint ChannelMask;
    [FieldOffset(24)] public Guid SubFormat;

    public const ushort Pcm = 1, IeeeFloat = 3, Extensible = 0xFFFE;
}

/// <summary>PROPERTYKEY: a property's set and number.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
}

/// <summary>A PROPVARIANT (24 bytes on 64-bit Windows), read for its type and its first pointer-sized value.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariantValue
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public nint Pointer;

    public const ushort LpWStr = 31;
}

internal static unsafe partial class AudioNative
{
    public static readonly Guid DeviceEnumeratorClass = new("bcde0395-e52f-467c-8e3d-c4579291692e");
    public static readonly Guid DeviceEnumeratorId = new("a95664d2-9614-4f35-a746-de8db63617e6");
    public static readonly Guid AudioClientId = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
    public static readonly Guid AudioClient3Id = new("7ed4ee07-8e67-4cd4-8c1a-2b7a5987ad42");
    public static readonly Guid RenderClientId = new("f294acfc-3146-4483-a7bf-addca7c260e2");
    public static readonly Guid AudioClockId = new("cd63314f-3fba-4a1b-812c-ef96358728e7");
    public static readonly Guid NotificationClientId = new("7991eec9-7e89-4d85-8390-6c703cec60c0");
    public static readonly Guid UnknownId = new("00000000-0000-0000-c000-000000000046");
    public static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>PKEY_Device_FriendlyName: "Speakers (Realtek(R) Audio)".</summary>
    public static readonly PropertyKey DeviceFriendlyName = new() { FormatId = new("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };

    public const int Render = 0, Console = 0;
    public const uint DeviceStateActive = 1;
    public const uint ClassContextAll = 0x17;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint BufferFlagsSilent = 0x2;
    public const int DeviceInvalidated = unchecked((int)0x88890004);
    public const int ServiceNotRunning = unchecked((int)0x88890010);
    public const int ResourcesInvalidated = unchecked((int)0x88890026);
    public const uint WaitObject0 = 0, WaitTimeout = 0x102, Infinite = 0xFFFFFFFF;

    /// <summary>Whether a WASAPI failure means the endpoint went away (unplugged, disabled, the service restarted): open again.</summary>
    public static bool IsDeviceGone(int hr) => hr is DeviceInvalidated or ServiceNotRunning or ResourcesInvalidated;

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(Guid* classId, nint outer, uint context, Guid* interfaceId, nint* instance);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(PropVariantValue* value);

    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint AvSetMmThreadCharacteristicsW(string task, uint* taskIndex);

    [LibraryImport("avrt.dll")]
    public static partial int AvRevertMmThreadCharacteristics(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial nint CreateEventW(nint attributes, int manualReset, int initialState, nint name);

    [LibraryImport("kernel32.dll")]
    public static partial int SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    /// <summary>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION: a timer that wakes to the half millisecond, not the 15.6 ms tick.</summary>
    public const uint HighResolutionTimer = 0x2, TimerAllAccess = 0x1F0003;

    [LibraryImport("kernel32.dll")]
    public static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll")]
    public static partial int SetWaitableTimer(nint timer, long* dueTime, int period, nint completion, nint argument, int resume);

    // ----- IMMDeviceEnumerator -----

    public static int EnumAudioEndpoints(nint enumerator, int flow, uint stateMask, out nint collection)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged<nint, int, uint, nint*, int>)Com.Table(enumerator)[3])(enumerator, flow, stateMask, &result);
        collection = result;
        return hr;
    }

    public static int GetDefaultAudioEndpoint(nint enumerator, int flow, int role, out nint device)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged<nint, int, int, nint*, int>)Com.Table(enumerator)[4])(enumerator, flow, role, &result);
        device = result;
        return hr;
    }

    public static int GetDevice(nint enumerator, string id, out nint device)
    {
        nint result = 0;
        int hr;
        fixed (char* text = id)
            hr = ((delegate* unmanaged<nint, char*, nint*, int>)Com.Table(enumerator)[5])(enumerator, text, &result);
        device = result;
        return hr;
    }

    public static int RegisterEndpointNotificationCallback(nint enumerator, nint client) =>
        ((delegate* unmanaged<nint, nint, int>)Com.Table(enumerator)[6])(enumerator, client);

    public static int UnregisterEndpointNotificationCallback(nint enumerator, nint client) =>
        ((delegate* unmanaged<nint, nint, int>)Com.Table(enumerator)[7])(enumerator, client);

    // ----- IMMDeviceCollection -----

    public static uint CollectionCount(nint collection)
    {
        uint count = 0;
        return ((delegate* unmanaged<nint, uint*, int>)Com.Table(collection)[3])(collection, &count) < 0 ? 0 : count;
    }

    public static int CollectionItem(nint collection, uint index, out nint device)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged<nint, uint, nint*, int>)Com.Table(collection)[4])(collection, index, &result);
        device = result;
        return hr;
    }

    // ----- IMMDevice -----

    public static int Activate(nint device, in Guid interfaceId, out nint instance)
    {
        nint result = 0;
        int hr;
        fixed (Guid* id = &interfaceId)
            hr = ((delegate* unmanaged<nint, Guid*, uint, void*, nint*, int>)Com.Table(device)[3])(device, id, ClassContextAll, null, &result);
        instance = result;
        return hr;
    }

    /// <summary>The device's id ("{0.0.0.00000000}.{guid}"), or "" when it cannot say.</summary>
    public static string DeviceId(nint device)
    {
        char* text = null;
        if (((delegate* unmanaged<nint, char**, int>)Com.Table(device)[5])(device, &text) < 0 || text == null)
            return "";
        try
        {
            return new string(text);
        }
        finally
        {
            Com.CoTaskMemFree((nint)text);
        }
    }

    /// <summary>The device's friendly name from its property store, or "" when it has none.</summary>
    public static string DeviceName(nint device)
    {
        nint store = 0;
        if (((delegate* unmanaged<nint, uint, nint*, int>)Com.Table(device)[4])(device, 0, &store) < 0 || store == 0)
            return "";
        try
        {
            var key = DeviceFriendlyName;
            var value = default(PropVariantValue);
            if (((delegate* unmanaged<nint, PropertyKey*, PropVariantValue*, int>)Com.Table(store)[5])(store, &key, &value) < 0)
                return "";
            try
            {
                return value.Type == PropVariantValue.LpWStr && value.Pointer != 0 ? new string((char*)value.Pointer) : "";
            }
            finally
            {
                PropVariantClear(&value);
            }
        }
        finally
        {
            Com.Release(ref store);
        }
    }

    // ----- IAudioClient (3-14), IAudioClient2 (15-17), IAudioClient3 (18-20) -----

    public static int Initialize(nint client, uint flags, long bufferDuration, WaveFormat* format) =>
        ((delegate* unmanaged<nint, int, uint, long, long, WaveFormat*, Guid*, int>)Com.Table(client)[3])(client, 0, flags, bufferDuration, 0, format, null);

    public static int GetBufferSize(nint client, out uint frames)
    {
        uint result = 0;
        var hr = ((delegate* unmanaged<nint, uint*, int>)Com.Table(client)[4])(client, &result);
        frames = result;
        return hr;
    }

    public static int GetStreamLatency(nint client, out long latency)
    {
        long result = 0;
        var hr = ((delegate* unmanaged<nint, long*, int>)Com.Table(client)[5])(client, &result);
        latency = result;
        return hr;
    }

    public static int GetCurrentPadding(nint client, out uint frames)
    {
        uint result = 0;
        var hr = ((delegate* unmanaged<nint, uint*, int>)Com.Table(client)[6])(client, &result);
        frames = result;
        return hr;
    }

    /// <summary>The shared-mode mix format, allocated by COM (free it with CoTaskMemFree).</summary>
    public static int GetMixFormat(nint client, out WaveFormat* format)
    {
        WaveFormat* result = null;
        var hr = ((delegate* unmanaged<nint, WaveFormat**, int>)Com.Table(client)[8])(client, &result);
        format = result;
        return hr;
    }

    public static int GetDevicePeriod(nint client, out long defaultPeriod, out long minimumPeriod)
    {
        long d = 0, m = 0;
        var hr = ((delegate* unmanaged<nint, long*, long*, int>)Com.Table(client)[9])(client, &d, &m);
        (defaultPeriod, minimumPeriod) = (d, m);
        return hr;
    }

    public static int Start(nint client) => ((delegate* unmanaged<nint, int>)Com.Table(client)[10])(client);

    public static int Stop(nint client) => ((delegate* unmanaged<nint, int>)Com.Table(client)[11])(client);

    public static int Reset(nint client) => ((delegate* unmanaged<nint, int>)Com.Table(client)[12])(client);

    public static int SetEventHandle(nint client, nint handle) => ((delegate* unmanaged<nint, nint, int>)Com.Table(client)[13])(client, handle);

    public static int GetService(nint client, in Guid interfaceId, out nint service)
    {
        nint result = 0;
        int hr;
        fixed (Guid* id = &interfaceId)
            hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)Com.Table(client)[14])(client, id, &result);
        service = result;
        return hr;
    }

    public static int GetSharedModeEnginePeriod(nint client3, WaveFormat* format, out uint defaultFrames, out uint fundamentalFrames, out uint minimumFrames, out uint maximumFrames)
    {
        uint d = 0, f = 0, mn = 0, mx = 0;
        var hr = ((delegate* unmanaged<nint, WaveFormat*, uint*, uint*, uint*, uint*, int>)Com.Table(client3)[18])(client3, format, &d, &f, &mn, &mx);
        (defaultFrames, fundamentalFrames, minimumFrames, maximumFrames) = (d, f, mn, mx);
        return hr;
    }

    public static int InitializeSharedAudioStream(nint client3, uint flags, uint periodFrames, WaveFormat* format) =>
        ((delegate* unmanaged<nint, uint, uint, WaveFormat*, Guid*, int>)Com.Table(client3)[20])(client3, flags, periodFrames, format, null);

    // ----- IAudioRenderClient -----

    public static int GetBuffer(nint render, uint frames, out byte* data)
    {
        byte* result = null;
        var hr = ((delegate* unmanaged<nint, uint, byte**, int>)Com.Table(render)[3])(render, frames, &result);
        data = result;
        return hr;
    }

    public static int ReleaseBuffer(nint render, uint frames, uint flags) =>
        ((delegate* unmanaged<nint, uint, uint, int>)Com.Table(render)[4])(render, frames, flags);

    // ----- IAudioClock -----

    public static int GetFrequency(nint clock, out ulong frequency)
    {
        ulong result = 0;
        var hr = ((delegate* unmanaged<nint, ulong*, int>)Com.Table(clock)[3])(clock, &result);
        frequency = result;
        return hr;
    }

    /// <summary>The position of the sample at the speakers (units of the clock's frequency), and when (QPC, 100 ns).</summary>
    public static int GetPosition(nint clock, out ulong position, out ulong qpc100ns)
    {
        ulong p = 0, q = 0;
        var hr = ((delegate* unmanaged<nint, ulong*, ulong*, int>)Com.Table(clock)[4])(clock, &p, &q);
        (position, qpc100ns) = (p, q);
        return hr;
    }
}

/// <summary>The Media Foundation pieces the audio decoder adds to the compositor's (MfGuids, Mf).</summary>
internal static unsafe class MfAudio
{
    public static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71");
    public static readonly Guid Float = new("00000003-0000-0010-8000-00aa00389b71");
    public static readonly Guid SamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid NumChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid ChannelMask = new("55fb5765-644a-4caf-8479-938983bb1588");

    /// <summary>IMFSample::ConvertToContiguousBuffer: one buffer holding all of the sample.</summary>
    public static int ContiguousBuffer(nint sample, out nint buffer)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged<nint, nint*, int>)Com.Table(sample)[41])(sample, &result);
        buffer = result;
        return hr;
    }

    // MFStartup is counted by Media Foundation, so the sound engine's own is independent of the compositor's.
    private static int _started;
    private static readonly object StartGate = new();

    public static bool Start()
    {
        if (Volatile.Read(ref _started) == 1)
            return true;
        lock (StartGate)
        {
            if (_started == 1)
                return true;
            if (Mf.MFStartup(Mf.Version, Mf.StartupNoSocket) < 0)
                return false;
            Volatile.Write(ref _started, 1);
            return true;
        }
    }
}
