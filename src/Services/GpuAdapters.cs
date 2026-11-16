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

    /// <summary>Driver version in Windows format (e.g. 31.0.24033.1003); empty when unreadable.</summary>
    public string DriverVersion { get; init; } = "";

    /// <summary>Its own video memory in MB (DXGI's DedicatedVideoMemory); 0 for a card that shares the computer's.</summary>
    public long DedicatedVideoMemoryMb { get; init; }

    /// <summary>DXGI adapter LUID (low and high parts packed into a long), used to filter MFTEnum2.</summary>
    public long Luid { get; init; }

    public bool IsNvidia => VendorId == 0x10DE;

    public bool IsAmd => VendorId == 0x1002;

    public bool IsIntel => VendorId == 0x8086;

    /// <summary>True for Qualcomm Adreno: PCI vendor 0x5143, 'QCOM' (Windows on ARM), or a matching name.</summary>
    public bool IsQualcomm => VendorId is 0x5143 or 0x4D4F4351
                              || Name.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) || Name.Contains("Adreno", StringComparison.OrdinalIgnoreCase);

    public string Display => Index < 0 ? Name : string.Create(CultureInfo.InvariantCulture, $"{Index}: {Name}");

    public override string ToString() => Display;
}

/// <summary>
/// GPU adapters enumerated via DXGI (no managed API); DXGI order matches FFmpeg's D3D11 device indices.
/// </summary>
public static partial class GpuAdapters
{
    /// <summary>One DXGI enumeration snapshot: adapters plus WARP index, published atomically.</summary>
    private sealed record Listing(IReadOnlyList<GpuAdapter> Adapters, int? WarpIndex);

    private static Listing? _listing;
    private static readonly Lock ListGate = new();

    /// <summary>
    /// Lazily enumerated once under a lock (concurrent post-Frame-1 callers wait; enumeration loads drivers, up to 250 ms). A single
    /// reference keeps adapters and WARP index consistent. Failed enumerations are not cached.
    /// </summary>
    private static Listing Listed => Volatile.Read(ref _listing) ?? ListOnce();

    private static Listing ListOnce()
    {
        lock (ListGate)
        {
            if (Volatile.Read(ref _listing) is { } listed)
                return listed;
            var made = Enumerate();
            Volatile.Write(ref _listing, made);
            return made;
        }
    }

    /// <summary>Every card that can decode, in DXGI's order. Empty when DXGI could not be asked.</summary>
    public static IReadOnlyList<GpuAdapter> All => Listed.Adapters;

    /// <summary>
    /// True once <see cref="All"/> has enumerated; callers that must not trigger enumeration check this first (pre-Frame-1 encoder logic).
    /// </summary>
    public static bool IsListed => Volatile.Read(ref _listing) is not null;

    /// <summary>
    /// Test hook: overrides the listing (null = not yet listed) until disposed, which restores the original (WARP index included).
    /// <see cref="SetListingForTests"/> changes it within the scope.
    /// </summary>
    internal static IDisposable ListingForTests(IReadOnlyList<GpuAdapter>? cards)
    {
        var kept = Volatile.Read(ref _listing);
        SetListingForTests(cards);
        return new ListingRestore(kept);
    }

    /// <summary>Replaces the listing inside a <see cref="ListingForTests"/> scope.</summary>
    internal static void SetListingForTests(IReadOnlyList<GpuAdapter>? cards) => Volatile.Write(ref _listing, cards is null ? null : new Listing(cards, null));

    private sealed class ListingRestore(Listing? kept) : IDisposable
    {
        public void Dispose() => Volatile.Write(ref _listing, kept);
    }

    /// <summary>True when an Adreno is present (reachable via MF encoders; see EncoderOption.FromHardwareName).</summary>
    public static bool HasQualcommAdreno => All.Any(a => a.IsQualcomm);

    /// <summary>
    /// DXGI index of WARP ("Microsoft Basic Render Driver", excluded from <see cref="All"/>), the GPU preview's last-resort fallback
    /// (MainWindow.OnGpuFailed). Null when absent.
    /// </summary>
    public static int? WarpIndex => Listed.WarpIndex;

    // (The legacy "-hwaccel auto" DecodeArguments was removed with the hardware-decoding switch.)

    /// <summary>
    /// DXGI index of the compositor's adapter as reported by its worker (GpuPreviewEngine); -1 until known. Defines Auto for background work.
    /// </summary>
    public static int CompositorIndex { get; set; } = -1;

    /// <summary>
    /// Compositor adapter: the Settings choice, else the worker-reported one (<see cref="CompositorIndex"/>, the high-performance GPU);
    /// null while unknown (an Auto worker then picks the high-performance GPU itself).
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
    /// Adapter for background decode and proxy encode: the Settings choice; Auto means the compositor adapter
    /// (<see cref="CompositorAdapter"/>), never a secondary GPU on its own (strip workers on a secondary Intel Arc caused VIDEO_TDR_FAILURE
    /// 0x116). Null for CPU, or while the compositor adapter is unknown (strip workers then follow the Compositor GPU setting).
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

    /// <summary>True when the background adapter differs from the compositor adapter.</summary>
    public static bool HasSeparateBackgroundCard =>
        BackgroundAdapter is { } card && (CompositorAdapter ?? All.FirstOrDefault()) is { } player && (card.VendorId, card.DeviceId) != (player.VendorId, player.DeviceId);

    /// <summary>
    /// Background decode options: D3D11 on the background adapter; FFmpeg's default when unknown; none (CPU) when Settings says so.
    /// </summary>
    public static IReadOnlyList<string> BackgroundDecodeArguments => AppSettings.Current.BackgroundGpu == -2 ? []
        : BackgroundAdapter is { } card ? ["-hwaccel", "d3d11va", "-hwaccel_device", card.Index.ToString(CultureInfo.InvariantCulture)]
        : ["-hwaccel", "auto"];

    /// <summary>
    /// First DXGI adapter of the encoder family's vendor (QSV/Intel, AMF/AMD, NVENC/NVIDIA) for the Live Encoded Preview; null if none.
    /// </summary>
    public static GpuAdapter? OfVendor(uint vendorId) => vendorId == 0 ? null : All.FirstOrDefault(a => a.VendorId == vendorId);

    /// <summary>Background tool thread option (Settings "Threads per background tool"); none for Auto.</summary>
    public static IReadOnlyList<string> BackgroundThreadArguments => AppSettings.Current.BackgroundToolThreads > 0
        ? ["-threads", Math.Clamp(AppSettings.Current.BackgroundToolThreads, 1, 64).ToString(CultureInfo.InvariantCulture)]
        : [];

    // IDXGIFactory1, made by the compositor's one CreateDXGIFactory1 declaration.
    private static unsafe int CreateFactory(out IntPtr factory)
    {
        var id = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        nint made = 0;
        var hr = Compositor.Native.Dxgi.CreateDXGIFactory1(&id, &made);
        factory = made;
        return hr;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdaptersDelegate(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescDelegate(IntPtr adapter, IntPtr description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CheckInterfaceSupportDelegate(IntPtr adapter, in Guid interfaceId, out long driverVersion);

    // Places in the COM method tables: IDXGIFactory::EnumAdapters, IDXGIAdapter::GetDesc and CheckInterfaceSupport.

    // CheckInterfaceSupport(IDXGIDevice) returns the UMD driver version (DXGI's only source; browsers use it too).
    private static readonly Guid DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    /// <summary>
    /// One-line adapter + driver fingerprint for change detection (e.g. the encoder probe cache).
    /// </summary>
    public static string Fingerprint => string.Join(";", All.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.VendorId:X4}:{a.DeviceId:X4}:{a.DriverVersion}")));

    // DXGI_ADAPTER_DESC: Description (128 UTF-16), VendorId, DeviceId, SubSysId, Revision, three SIZE_T memory sizes
    // (dedicated video, dedicated system, shared system), AdapterLuid.
    private const int DescriptionBytes = 256;
    private const int DedicatedVideoMemoryOffset = DescriptionBytes + 16;
    private const int LuidOffset = DedicatedVideoMemoryOffset + 24;
    private const int DescriptionSize = 320;

    // The software renderer Windows lists after the real cards.
    private const uint MicrosoftVendor = 0x1414;
    private const uint BasicRenderDevice = 0x8C;

    private static Listing Enumerate()
    {
        var adapters = new List<GpuAdapter>();
        int? warpIndex = null;
        var factory = IntPtr.Zero;
        try
        {
            if (CreateFactory(out factory) < 0 || factory == IntPtr.Zero)
                return new Listing(adapters, null);

            var enumerate = Method<EnumAdaptersDelegate>(factory, Compositor.Native.IDXGIFactorySlot.EnumAdapters);
            for (uint index = 0; index < 16 && enumerate(factory, index, out var adapter) >= 0 && adapter != IntPtr.Zero; index++)
            {
                var description = Marshal.AllocHGlobal(DescriptionSize);
                try
                {
                    if (Method<GetDescDelegate>(adapter, Compositor.Native.IDXGIAdapterSlot.GetDesc)(adapter, description) < 0)
                        continue;

                    var (vendor, device) = ((uint)Marshal.ReadInt32(description, DescriptionBytes), (uint)Marshal.ReadInt32(description, DescriptionBytes + 4));
                    if (vendor == MicrosoftVendor && device == BasicRenderDevice)
                    {
                        warpIndex = (int)index;
                        continue;
                    }

                    var name = (Marshal.PtrToStringUni(description, DescriptionBytes / 2) ?? "").TrimEnd('\0');
                    var end = name.IndexOf('\0');
                    var driver = Method<CheckInterfaceSupportDelegate>(adapter, Compositor.Native.IDXGIAdapterSlot.CheckInterfaceSupport)(adapter, DxgiDevice, out var version) >= 0
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

        return new Listing(adapters, warpIndex);
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
}
