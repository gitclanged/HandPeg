using System.Runtime.InteropServices;
using System.Text;

namespace HandPegApp.Compositor.Native;

// ----- DXCore adapter enumeration (GPUs and NPUs) -----
//
// DXGI lists graphics adapters only; DXCore (dxcore.dll, Windows 10 2004+) lists every WDDM/MCDM adapter, NPUs included, and reports
// IsIntegrated (unified memory). Loaded lazily, never linked: a missing DLL or failed call yields an empty list, never an exception.
// Hand-rolled COM like the rest of Native; vtable slots follow dxcore_interface.h declaration order (IUnknown 0-2, then the
// interface's own), verified on Snapdragon X Elite hardware.

/// <summary>One adapter as listed by DXCore.</summary>
/// <param name="Luid">Adapter LUID (the same value DXGI reports).</param>
/// <param name="IsIntegrated">Unified memory (SoC graphics); null when the driver does not report it.</param>
/// <param name="IsNpu">DXCORE_HARDWARE_TYPE_ATTRIBUTE_NPU, or (older Windows without that attribute) a hardware ML adapter without graphics.</param>
/// <param name="IsGraphics">Supports D3D11 or D3D12 graphics.</param>
/// <param name="GenericMl">DXCORE_ADAPTER_ATTRIBUTE_D3D12_GENERIC_ML: DirectML and ONNX Runtime can target it.</param>
internal sealed record DxCoreAdapter(
    long Luid, string Description, ulong DriverVersion, uint VendorId, uint DeviceId, bool IsHardware, bool? IsIntegrated,
    long DedicatedAdapterMemory, long SharedSystemMemory, bool IsNpu, bool IsGraphics, bool GenericMl)
{
    /// <summary>Driver version as Windows formats it (four 16-bit parts).</summary>
    public string DriverVersionText => DriverVersion == 0 ? ""
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{DriverVersion >> 48}.{(DriverVersion >> 32) & 0xFFFF}.{(DriverVersion >> 16) & 0xFFFF}.{DriverVersion & 0xFFFF}");
}

internal static unsafe class DxCore
{
    // Interface ids (dxcore_interface.h).
    private static readonly Guid AdapterFactoryId = new("78ee5945-c36e-4b13-a669-005dd11c0f06");
    private static readonly Guid AdapterListId = new("526c7776-40e9-459b-b711-f32ad76dfc28");
    private static readonly Guid AdapterId = new("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");

    // Attributes for filtering adapter lists and querying adapters.
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

    /// <summary>True when dxcore.dll loads and exports its factory (probed once, lazily).</summary>
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
    /// NPUs and other non-graphics hardware ML adapters from the NPU and generic-ML lists, deduplicated by LUID; empty without DXCore.
    /// Avoid the UI thread: drivers may respond slowly.
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

    /// <summary>Adapters with <paramref name="attribute"/>; empty without DXCore or when the call fails (attribute unknown to this Windows).</summary>
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

            // IDXCoreAdapterFactory::CreateAdapterList, then IDXCoreAdapterList::GetAdapterCount / GetAdapter.
            var listId = AdapterListId;
            if (((delegate* unmanaged<nint, uint, Guid*, Guid*, nint*, int>)Com.Table(factory)[IDXCoreAdapterFactorySlot.CreateAdapterList])(factory, 1, &attribute, &listId, &list) < 0 || list == 0)
                return adapters;
            var count = ((delegate* unmanaged<nint, uint>)Com.Table(list)[IDXCoreAdapterListSlot.GetAdapterCount])(list);
            for (uint i = 0; i < count && i < 64; i++)
            {
                nint adapter = 0;
                var adapterId = AdapterId;
                if (((delegate* unmanaged<nint, uint, Guid*, nint*, int>)Com.Table(list)[IDXCoreAdapterListSlot.GetAdapter])(list, i, &adapterId, &adapter) < 0 || adapter == 0)
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
            // A misbehaving driver ends enumeration; adapters read so far stand.
        }
        finally
        {
            Com.Release(ref list);
            Com.Release(ref factory);
        }

        return adapters;
    }

    /// <summary>IsIntegrated (unified memory) for the adapter with <paramref name="luid"/>; null without DXCore or when the driver does not report it.</summary>
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

            // IDXCoreAdapterFactory::GetAdapterByLuid takes the LUID by pointer.
            var adapterId = AdapterId;
            if (((delegate* unmanaged<nint, long*, Guid*, nint*, int>)Com.Table(factory)[IDXCoreAdapterFactorySlot.GetAdapterByLuid])(factory, &luid, &adapterId, &adapter) < 0 || adapter == 0)
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

    // IDXCoreAdapter::IsAttributeSupported returns a one-byte C++ bool.
    private static bool Has(nint adapter, Guid attribute) =>
        ((delegate* unmanaged<nint, Guid*, byte>)Com.Table(adapter)[IDXCoreAdapterSlot.IsAttributeSupported])(adapter, &attribute) != 0;

    // IDXCoreAdapter::IsPropertySupported.
    private static bool Supported(nint adapter, uint property) =>
        ((delegate* unmanaged<nint, uint, byte>)Com.Table(adapter)[IDXCoreAdapterSlot.IsPropertySupported])(adapter, property) != 0;

    // IDXCoreAdapter::GetProperty: copies the property into a buffer of the given size.
    private static bool Get(nint adapter, uint property, void* buffer, nuint size) =>
        ((delegate* unmanaged<nint, uint, nuint, void*, int>)Com.Table(adapter)[IDXCoreAdapterSlot.GetProperty])(adapter, property, size, buffer) >= 0;

    // IDXCoreAdapter::GetPropertySize.
    private static nuint Size(nint adapter, uint property)
    {
        nuint size = 0;
        return ((delegate* unmanaged<nint, uint, nuint*, int>)Com.Table(adapter)[IDXCoreAdapterSlot.GetPropertySize])(adapter, property, &size) >= 0 ? size : 0;
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
