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

    /// <summary>The card's LUID as DXGI gives it (its low and high parts in one long): what Media Foundation's MFTEnum2 narrows to (5.4.4).</summary>
    public long Luid { get; init; }

    public bool IsNvidia => VendorId == 0x10DE;

    public bool IsAmd => VendorId == 0x1002;

    public bool IsIntel => VendorId == 0x8086;

    /// <summary>A Qualcomm Adreno (a Snapdragon PC's graphics): PCI vendor 0x5143, or 'QCOM' as Windows on ARM reports it, or its name says so.</summary>
    public bool IsQualcomm => VendorId is 0x5143 or 0x4D4F4351
                              || Name.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) || Name.Contains("Adreno", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Whether the cards have been listed already (<see cref="All"/> asked once): what may read them without listing them, which loads
    /// each card's driver (up to a quarter of a second), asks this first (5.4B: EncoderProber.ProblemsForCardsHere, before the first frame).
    /// </summary>
    public static bool IsListed => _found is not null;

    /// <summary>A card here is a Qualcomm Adreno: the Media Foundation encoders reach it (EncoderOption.FromHardwareName names them so).</summary>
    public static bool HasQualcommAdreno => All.Any(a => a.IsQualcomm);

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

    // (DecodeArguments, the "-hwaccel auto" of the dead Enable Hardware Decoding switch, went with it, 5.4B: no export read it.)

    /// <summary>
    /// The DXGI number of the card the player's compositor draws on, once its worker said so (GpuPreviewEngine); -1 until then.
    /// What Auto means for the background card (5.4.4).
    /// </summary>
    public static int CompositorIndex { get; set; } = -1;

    /// <summary>
    /// The card the player's compositor draws on: the Compositor GPU chosen in Settings, else the one its worker reported
    /// (<see cref="CompositorIndex"/>, Windows' high-performance card); null while neither is known (a worker started on Auto then
    /// takes the high-performance card itself, the compositor's).
    /// </summary>
    public static GpuAdapter? CompositorAdapter
    {
        get
        {
            var chosen = AppSettings.Current.CompositorGpuAdapter;
            var index = chosen >= 0 && All.Any(a => a.Index == chosen) ? chosen : CompositorIndex;
            return index >= 0 ? All.FirstOrDefault(a => a.Index == index) : null;
        }
    }

    /// <summary>
    /// The card background tools decode on (and proxies are encoded on), as Settings says: a card chosen there; with Auto
    /// (5.4.4: the Desktop's 0x116 VIDEO_TDR_FAILURE, the program strip and filmstrip workers on its Arc A310 beside the RX 7900 XTX),
    /// the Compositor GPU (<see cref="CompositorAdapter"/>), never a second card on its own; null for the processor, or while the
    /// compositor's card is not known yet (the strip workers then start on the Compositor GPU's setting, Auto the high-performance card).
    /// </summary>
    public static GpuAdapter? BackgroundAdapter
    {
        get
        {
            var choice = AppSettings.Current.BackgroundGpu;
            if (choice == -2)
                return null;
            if (choice >= 0 && All.FirstOrDefault(a => a.Index == choice) is { } chosen)
                return chosen;
            return CompositorAdapter;
        }
    }

    /// <summary>Whether background work has a card of its own: the background card is not the one the player draws on.</summary>
    public static bool HasSeparateBackgroundCard =>
        BackgroundAdapter is { } card && (CompositorAdapter ?? All.FirstOrDefault()) is { } player && (card.VendorId, card.DeviceId) != (player.VendorId, player.DeviceId);

    /// <summary>
    /// The options a background tool decodes with: Direct3D 11 on the background card; whatever FFmpeg finds when
    /// the card is not known yet; nothing (the processor) when Settings says so.
    /// </summary>
    public static IReadOnlyList<string> BackgroundDecodeArguments => AppSettings.Current.BackgroundGpu == -2 ? []
        : BackgroundAdapter is { } card ? ["-hwaccel", "d3d11va", "-hwaccel_device", card.Index.ToString(CultureInfo.InvariantCulture)]
        : ["-hwaccel", "auto"];

    /// <summary>
    /// The card of a hardware encoder family (5.4.4, the Live Encoded Preview's encoder): Intel's for QuickSync, AMD's for AMF,
    /// NVIDIA's for NVENC; the first of that vendor DXGI lists. Null when there is none (or the family names no vendor).
    /// </summary>
    public static GpuAdapter? OfVendor(uint vendorId) => vendorId == 0 ? null : All.FirstOrDefault(a => a.VendorId == vendorId);

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
    // the dedicated video memory, the dedicated system memory and the shared system memory (three SIZE_Ts), then the LUID.
    private const int DescriptionBytes = 256;
    private const int DedicatedVideoMemoryOffset = DescriptionBytes + 16;
    private const int LuidOffset = DedicatedVideoMemoryOffset + 24;
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
                        Luid = IntPtr.Size == 8 ? Marshal.ReadInt64(description, LuidOffset) : 0,
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
