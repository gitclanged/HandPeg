using System.Globalization;
using System.Runtime.InteropServices;

namespace HandPegApp.Services;

/// <summary>One graphics card, as Direct3D numbers it: the number is what FFmpeg's -hwaccel_device takes.</summary>
public sealed record GpuAdapter(int Index, string Name)
{
    /// <summary>The choice that leaves it to FFmpeg: whichever card and decoder it finds first.</summary>
    public static GpuAdapter Automatic { get; } = new(-1, "Automatic");

    /// <summary>The PCI vendor: 0x10DE NVIDIA, 0x1002 AMD, 0x8086 Intel.</summary>
    public uint VendorId { get; init; }

    public uint DeviceId { get; init; }

    /// <summary>The version of its driver, as Windows numbers it (31.0.24033.1003); empty when it could not be read.</summary>
    public string DriverVersion { get; init; } = "";

    /// <summary>Its own video memory in MB (DXGI's DedicatedVideoMemory); 0 for a card that shares the computer's.</summary>
    public long DedicatedVideoMemoryMb { get; init; }

    public bool IsNvidia => VendorId == 0x10DE;

    public bool IsAmd => VendorId == 0x1002;

    public bool IsIntel => VendorId == 0x8086;

    public string Display => Index < 0 ? Name : string.Create(CultureInfo.InvariantCulture, $"{Index}: {Name}");

    public override string ToString() => Display;
}

/// <summary>
/// The graphics cards of this machine, asked of DXGI directly: there is no managed API for it, and the
/// order DXGI lists them in is the order FFmpeg's Direct3D 11 decoder counts them in.
/// </summary>
public static partial class GpuAdapters
{
    private static IReadOnlyList<GpuAdapter>? _found;

    /// <summary>Every card that can decode, in DXGI's order. Empty when DXGI could not be asked.</summary>
    public static IReadOnlyList<GpuAdapter> All => _found ??= Enumerate();

    private static int? _warpIndex;

    /// <summary>
    /// The DXGI index of the processor's software renderer (WARP, the "Microsoft Basic Render Driver" Windows lists after
    /// the cards; not one of <see cref="All"/>), for the GPU preview's last resort when the card fails (MainWindow.OnGpuFailed).
    /// Null when DXGI lists none.
    /// </summary>
    public static int? WarpIndex
    {
        get
        {
            _ = All;
            return _warpIndex;
        }
    }

    /// <summary>
    /// The FFmpeg options that switch hardware decoding on, with a space after them: whichever decoder FFmpeg
    /// finds, or Direct3D 11 on the card chosen in the settings.
    /// </summary>
    public static string DecodeArguments
    {
        get
        {
            var adapter = AppSettings.Current.HardwareDecodeAdapter;
            return adapter >= 0 && All.Any(a => a.Index == adapter)
                ? string.Create(CultureInfo.InvariantCulture, $"-hwaccel d3d11va -hwaccel_device {adapter} ")
                : "-hwaccel auto ";
        }
    }

    /// <summary>
    /// The card background tools decode on (and proxies are encoded on), as Settings says: a card chosen there;
    /// with Auto, a card other than the one the player draws on (DXGI's first, the one driving the main display),
    /// when there is one; null for the processor, or when there is only the player's card (that is FFmpeg's choice).
    /// </summary>
    public static GpuAdapter? BackgroundAdapter
    {
        get
        {
            var choice = AppSettings.Current.BackgroundGpu;
            if (choice == -2)
                return null;
            if (choice >= 0)
                return All.FirstOrDefault(a => a.Index == choice);

            var player = All.FirstOrDefault();
            return All.FirstOrDefault(a => player is not null && (a.VendorId, a.DeviceId) != (player.VendorId, player.DeviceId));
        }
    }

    /// <summary>Whether background work has a card of its own: the background card is not the one the player draws on.</summary>
    public static bool HasSeparateBackgroundCard =>
        BackgroundAdapter is { } card && All.FirstOrDefault() is { } player && (card.VendorId, card.DeviceId) != (player.VendorId, player.DeviceId);

    /// <summary>
    /// The options a background tool decodes with: Direct3D 11 on the background card; whatever FFmpeg finds when
    /// the choice is left to it and there is no second card; nothing (the processor) when Settings says so.
    /// </summary>
    public static IReadOnlyList<string> BackgroundDecodeArguments => AppSettings.Current.BackgroundGpu == -2 ? []
        : BackgroundAdapter is { } card ? ["-hwaccel", "d3d11va", "-hwaccel_device", card.Index.ToString(CultureInfo.InvariantCulture)]
        : ["-hwaccel", "auto"];

    /// <summary>The thread option a background tool is given (Settings, "Threads per background tool"); none for Auto.</summary>
    public static IReadOnlyList<string> BackgroundThreadArguments => AppSettings.Current.BackgroundToolThreads > 0
        ? ["-threads", Math.Clamp(AppSettings.Current.BackgroundToolThreads, 1, 64).ToString(CultureInfo.InvariantCulture)]
        : [];

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid interfaceId, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdaptersDelegate(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescDelegate(IntPtr adapter, IntPtr description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CheckInterfaceSupportDelegate(IntPtr adapter, in Guid interfaceId, out long driverVersion);

    // Places in the COM method tables: IDXGIFactory::EnumAdapters, IDXGIAdapter::GetDesc and CheckInterfaceSupport.
    private const int EnumAdaptersSlot = 7;
    private const int GetDescSlot = 8;
    private const int CheckInterfaceSupportSlot = 9;

    // Asked about IDXGIDevice, CheckInterfaceSupport answers with the version of the card's driver: the one way
    // DXGI tells it (as browsers read it too).
    private static readonly Guid DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    /// <summary>
    /// The cards and their drivers in one line, for anything that has to know whether they have changed since it
    /// last looked (the encoder probe's saved result).
    /// </summary>
    public static string Fingerprint => string.Join(";", All.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.VendorId:X4}:{a.DeviceId:X4}:{a.DriverVersion}")));

    // DXGI_ADAPTER_DESC: the name (128 UTF-16 characters), then the vendor, device, subsystem and revision numbers, then
    // the dedicated video memory (a SIZE_T).
    private const int DescriptionBytes = 256;
    private const int DedicatedVideoMemoryOffset = DescriptionBytes + 16;
    private const int DescriptionSize = 320;

    // The software renderer Windows lists after the real cards.
    private const uint MicrosoftVendor = 0x1414;
    private const uint BasicRenderDevice = 0x8C;

    private static List<GpuAdapter> Enumerate()
    {
        var adapters = new List<GpuAdapter>();
        var factory = IntPtr.Zero;
        try
        {
            if (CreateDXGIFactory1(new Guid("770aae78-f26f-4dba-a829-253c83d1b387"), out factory) < 0 || factory == IntPtr.Zero)
                return adapters;

            var enumerate = Method<EnumAdaptersDelegate>(factory, EnumAdaptersSlot);
            for (uint index = 0; index < 16 && enumerate(factory, index, out var adapter) >= 0 && adapter != IntPtr.Zero; index++)
            {
                var description = Marshal.AllocHGlobal(DescriptionSize);
                try
                {
                    if (Method<GetDescDelegate>(adapter, GetDescSlot)(adapter, description) < 0)
                        continue;

                    var (vendor, device) = ((uint)Marshal.ReadInt32(description, DescriptionBytes), (uint)Marshal.ReadInt32(description, DescriptionBytes + 4));
                    if (vendor == MicrosoftVendor && device == BasicRenderDevice)
                    {
                        _warpIndex = (int)index;
                        continue;
                    }

                    var name = (Marshal.PtrToStringUni(description, DescriptionBytes / 2) ?? "").TrimEnd('\0');
                    var end = name.IndexOf('\0');
                    var driver = Method<CheckInterfaceSupportDelegate>(adapter, CheckInterfaceSupportSlot)(adapter, DxgiDevice, out var version) >= 0
                        ? string.Create(CultureInfo.InvariantCulture, $"{(version >> 48) & 0xFFFF}.{(version >> 32) & 0xFFFF}.{(version >> 16) & 0xFFFF}.{version & 0xFFFF}")
                        : "";
                    adapters.Add(new GpuAdapter((int)index, (end >= 0 ? name[..end] : name).Trim())
                    {
                        VendorId = vendor,
                        DeviceId = device,
                        DriverVersion = driver,
                        DedicatedVideoMemoryMb = (long)((ulong)Marshal.ReadIntPtr(description, DedicatedVideoMemoryOffset) / (1024 * 1024)),
                    });
                }
                finally
                {
                    Marshal.FreeHGlobal(description);
                    Marshal.Release(adapter);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or AccessViolationException or MarshalDirectiveException)
        {
            // No DXGI to ask: the choice of card is simply not offered.
        }
        finally
        {
            if (factory != IntPtr.Zero)
                Marshal.Release(factory);
        }

        return adapters;
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
}
