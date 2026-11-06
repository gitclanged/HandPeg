using System.Runtime.InteropServices;
using System.Text;

namespace HandPegApp.Compositor.Native;

// ----- DXCore: every adapter Windows has, graphics cards and NPUs alike (Pass 5.2-Arm64AndNpu) -----
//
// DXGI lists graphics cards only; DXCore (dxcore.dll, Windows 10 2004 and later) lists every adapter with a WDDM or MCDM
// driver, among them the NPUs (a Snapdragon X's Hexagon, an Intel or AMD NPU), and says of a card whether it shares the
// computer's memory (IsIntegrated). The library is loaded when first asked for, never linked: a Windows without it (or a
// call that fails) gives an empty list, never an exception. COM by hand as the rest of Native: the method tables'
// places are dxcore_interface.h's declaration order (IUnknown 0-2, then the interface's own), checked on the Snapdragon X
// Elite by calling them (the factory's CreateAdapterList with the NPU attribute listed the Hexagon NPU; the adapter's
// IsAttributeSupported, IsPropertySupported, GetProperty and GetPropertySize gave its name, driver and memory).

/// <summary>One adapter as DXCore lists it.</summary>
/// <param name="Luid">Its LUID (the same number DXGI gives a graphics card).</param>
/// <param name="IsIntegrated">Shares the computer's memory (a unified-memory SoC's graphics); null when the driver does not say.</param>
/// <param name="IsNpu">DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU, or (a Windows that does not know that attribute) a hardware adapter for machine learning that draws no graphics.</param>
/// <param name="IsGraphics">It runs Direct3D 11 or 12 graphics: a graphics card.</param>
/// <param name="GenericMl">DXCORE_ADAPTER_ATTRIBUTE_D3D12_GENERIC_ML: DirectML and ONNX Runtime can run models on it.</param>
internal sealed record DxCoreAdapter(
    long Luid, string Description, ulong DriverVersion, uint VendorId, uint DeviceId, bool IsHardware, bool? IsIntegrated,
    long DedicatedAdapterMemory, long SharedSystemMemory, bool IsNpu, bool IsGraphics, bool GenericMl)
{
    /// <summary>The driver's version as Windows writes it (four parts of 16 bits).</summary>
    public string DriverVersionText => DriverVersion == 0 ? ""
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{DriverVersion >> 48}.{(DriverVersion >> 32) & 0xFFFF}.{(DriverVersion >> 16) & 0xFFFF}.{DriverVersion & 0xFFFF}");
}

internal static unsafe class DxCore
{
    // The interfaces (dxcore_interface.h).
    private static readonly Guid AdapterFactoryId = new("78ee5945-c36e-4b13-a669-005dd11c0f06");
    private static readonly Guid AdapterListId = new("526c7776-40e9-459b-b711-f32ad76dfc28");
    private static readonly Guid AdapterId = new("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");

    // The attributes an adapter list is filtered by, and an adapter asked about.
    public static readonly Guid D3D11Graphics = new("8c47866b-7583-450d-f0f0-6bada895af4b");
    public static readonly Guid D3D12Graphics = new("0c9ece4d-2f6e-4f01-8c96-e89e331b47b1");
    public static readonly Guid D3D12CoreCompute = new("248e2800-a793-4724-abaa-23a6de1be090");
    public static readonly Guid D3D12GenericMl = new("b71b0d41-1088-422f-a27c-0250b7d3a988");
    public static readonly Guid HardwareTypeNpu = new("d46140c4-add7-451b-9e56-06fe8c3b58ed");

    // DXCoreAdapterProperty.
    private const uint InstanceLuid = 0, DriverVersion = 1, DriverDescription = 2, HardwareId = 3, DedicatedAdapterMemory = 7,
        SharedSystemMemory = 9, IsHardware = 11, IsIntegrated = 12;

    private static readonly object Gate = new();
    private static bool _loaded;
    private static delegate* unmanaged<Guid*, nint*, int> _createFactory;

    /// <summary>Whether dxcore.dll is there and gives its factory (loaded the first time this is asked, once).</summary>
    public static bool IsAvailable
    {
        get
        {
            lock (Gate)
            {
                if (!_loaded)
                {
                    _loaded = true;
                    if (NativeLibrary.TryLoad("dxcore.dll", typeof(DxCore).Assembly, DllImportSearchPath.System32, out var library)
                        && NativeLibrary.TryGetExport(library, "DXCoreCreateAdapterFactory", out var export))
                    {
                        _createFactory = (delegate* unmanaged<Guid*, nint*, int>)export;
                    }
                }

                return _createFactory != null;
            }
        }
    }

    /// <summary>
    /// The NPUs (and the other hardware adapters for machine learning that draw no graphics): every adapter of the NPU list and
    /// of the machine learning list that is one, each once. Empty without DXCore, or with none. Not on the UI thread: a driver
    /// may take its time to answer.
    /// </summary>
    public static List<DxCoreAdapter> Npus()
    {
        var found = new List<DxCoreAdapter>();
        foreach (var adapter in AdaptersWith(HardwareTypeNpu).Concat(AdaptersWith(D3D12GenericMl)))
        {
            if (adapter.IsNpu && found.All(a => a.Luid != adapter.Luid))
                found.Add(adapter);
        }

        return found;
    }

    /// <summary>Every adapter DXCore lists with <paramref name="attribute"/>; empty without DXCore or when the call fails (an attribute this Windows does not know).</summary>
    public static List<DxCoreAdapter> AdaptersWith(Guid attribute)
    {
        var adapters = new List<DxCoreAdapter>();
        if (!IsAvailable)
            return adapters;

        nint factory = 0, list = 0;
        try
        {
            var factoryId = AdapterFactoryId;
            if (_createFactory(&factoryId, &factory) < 0 || factory == 0)
                return adapters;

            // IDXCoreAdapterFactory::CreateAdapterList (3), IDXCoreAdapterList::GetAdapterCount (4) and GetAdapter (3).
            var listId = AdapterListId;
            if (((delegate* unmanaged<nint, uint, Guid*, Guid*, nint*, int>)Com.Table(factory)[3])(factory, 1, &attribute, &listId, &list) < 0 || list == 0)
                return adapters;
            var count = ((delegate* unmanaged<nint, uint>)Com.Table(list)[4])(list);
            for (uint i = 0; i < count && i < 64; i++)
            {
                nint adapter = 0;
                var adapterId = AdapterId;
                if (((delegate* unmanaged<nint, uint, Guid*, nint*, int>)Com.Table(list)[3])(list, i, &adapterId, &adapter) < 0 || adapter == 0)
                    continue;
                try
                {
                    adapters.Add(Describe(adapter));
                }
                finally
                {
                    Com.Release(ref adapter);
                }
            }
        }
        catch (Exception ex) when (ex is SEHException or AccessViolationException or InvalidOperationException)
        {
            // A driver that misbehaves is no adapter worth naming: what was read stands.
        }
        finally
        {
            Com.Release(ref list);
            Com.Release(ref factory);
        }

        return adapters;
    }

    /// <summary>
    /// Whether the graphics card of <paramref name="luid"/> shares the computer's memory (IsIntegrated); null when DXCore is not
    /// there or its driver does not say.
    /// </summary>
    public static bool? IsIntegratedCard(long luid)
    {
        if (!IsAvailable)
            return null;

        nint factory = 0, adapter = 0;
        try
        {
            var factoryId = AdapterFactoryId;
            if (_createFactory(&factoryId, &factory) < 0 || factory == 0)
                return null;

            // IDXCoreAdapterFactory::GetAdapterByLuid (4): the LUID by reference, as DXGI's.
            var adapterId = AdapterId;
            if (((delegate* unmanaged<nint, long*, Guid*, nint*, int>)Com.Table(factory)[4])(factory, &luid, &adapterId, &adapter) < 0 || adapter == 0)
                return null;
            return Flag(adapter, IsIntegrated);
        }
        catch (Exception ex) when (ex is SEHException or AccessViolationException)
        {
            return null;
        }
        finally
        {
            Com.Release(ref adapter);
            Com.Release(ref factory);
        }
    }

    private static DxCoreAdapter Describe(nint adapter)
    {
        var luid = Number(adapter, InstanceLuid);
        var description = Text(adapter, DriverDescription);
        var version = (ulong)Number(adapter, DriverVersion);
        var (vendor, device) = (0u, 0u);
        var id = stackalloc uint[4];
        if (Supported(adapter, HardwareId) && Get(adapter, HardwareId, id, 16))
            (vendor, device) = (id[0], id[1]);

        var graphics = Has(adapter, D3D11Graphics) || Has(adapter, D3D12Graphics);
        var hardware = Flag(adapter, IsHardware) ?? true;
        var ml = Has(adapter, D3D12GenericMl);
        var npu = Has(adapter, HardwareTypeNpu) || (ml && hardware && !graphics);
        return new DxCoreAdapter(luid, description, version, vendor, device, hardware, Flag(adapter, IsIntegrated),
            Number(adapter, DedicatedAdapterMemory), Number(adapter, SharedSystemMemory), npu, graphics, ml);
    }

    // IDXCoreAdapter::IsAttributeSupported (4): a C++ bool, one byte.
    private static bool Has(nint adapter, Guid attribute) =>
        ((delegate* unmanaged<nint, Guid*, byte>)Com.Table(adapter)[4])(adapter, &attribute) != 0;

    // IDXCoreAdapter::IsPropertySupported (5).
    private static bool Supported(nint adapter, uint property) =>
        ((delegate* unmanaged<nint, uint, byte>)Com.Table(adapter)[5])(adapter, property) != 0;

    // IDXCoreAdapter::GetProperty (6): the property's bytes into a buffer of the size given.
    private static bool Get(nint adapter, uint property, void* buffer, nuint size) =>
        ((delegate* unmanaged<nint, uint, nuint, void*, int>)Com.Table(adapter)[6])(adapter, property, size, buffer) >= 0;

    // IDXCoreAdapter::GetPropertySize (7).
    private static nuint Size(nint adapter, uint property)
    {
        nuint size = 0;
        return ((delegate* unmanaged<nint, uint, nuint*, int>)Com.Table(adapter)[7])(adapter, property, &size) >= 0 ? size : 0;
    }

    private static long Number(nint adapter, uint property)
    {
        long value = 0;
        return Supported(adapter, property) && Size(adapter, property) == sizeof(long) && Get(adapter, property, &value, sizeof(long)) ? value : 0;
    }

    private static bool? Flag(nint adapter, uint property)
    {
        byte value = 0;
        return Supported(adapter, property) && Size(adapter, property) == 1 && Get(adapter, property, &value, 1) ? value != 0 : null;
    }

    private static string Text(nint adapter, uint property)
    {
        var size = Supported(adapter, property) ? Size(adapter, property) : 0;
        if (size is 0 or > 4096)
            return "";
        var bytes = stackalloc byte[(int)size];
        if (!Get(adapter, property, bytes, size))
            return "";
        var length = 0;
        while (length < (int)size && bytes[length] != 0)
            length++;
        return Encoding.UTF8.GetString(bytes, length).Trim();
    }
}
