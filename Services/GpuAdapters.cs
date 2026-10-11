using System.Globalization;
using System.Runtime.InteropServices;

namespace HandPegApp.Services;

/// <summary>One graphics card, as Direct3D numbers it: the number is what FFmpeg's -hwaccel_device takes.</summary>
public sealed record GpuAdapter(int Index, string Name)
{
    /// <summary>The choice that leaves it to FFmpeg: whichever card and decoder it finds first.</summary>
    public static GpuAdapter Automatic { get; } = new(-1, "Automatic");

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

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid interfaceId, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdaptersDelegate(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescDelegate(IntPtr adapter, IntPtr description);

    // Places in the COM method tables: IDXGIFactory::EnumAdapters and IDXGIAdapter::GetDesc.
    private const int EnumAdaptersSlot = 7;
    private const int GetDescSlot = 8;

    // DXGI_ADAPTER_DESC: the name (128 UTF-16 characters), then the vendor and device numbers.
    private const int DescriptionBytes = 256;
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
                        continue;

                    var name = (Marshal.PtrToStringUni(description, DescriptionBytes / 2) ?? "").TrimEnd('\0');
                    var end = name.IndexOf('\0');
                    adapters.Add(new GpuAdapter((int)index, (end >= 0 ? name[..end] : name).Trim()));
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
