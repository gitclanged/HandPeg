using System.Globalization;
using HandPegApp.Compositor.Native;

namespace HandPegApp.Services;

/// <summary>An NPU as enumerated by DXCore.</summary>
/// <param name="SharedMemoryMb">Usable system memory in MB (SoC NPUs have no dedicated memory).</param>
/// <param name="GenericMl">Supports DirectML / ONNX Runtime (DXCORE_ADAPTER_ATTRIBUTE_D3D12_GENERIC_ML).</param>
internal sealed record NpuAdapter(string Name, string Vendor, uint VendorId, uint DeviceId, string DriverVersion, long SharedMemoryMb, bool GenericMl);

/// <summary>
/// Local NPUs (e.g. Snapdragon Hexagon, Intel/AMD laptop NPUs) enumerated once via DXCore (Compositor.Native.DxCore, dxcore.dll loaded
/// lazily) on a pool thread when Settings or the benchmark first asks; never at startup. Empty when unavailable.
/// <para>Currently unused: installed whisper.cpp builds (CPU, CUDA, Adreno OpenCL) have no NPU back end; NPU captions would require a
/// QNN or ONNX Runtime speech model (AppSettings.CaptionAccelerator already exposes the option).</para>
/// </summary>
internal static class NpuAdapters
{
    private static readonly object Gate = new();
    private static Task<IReadOnlyList<NpuAdapter>>? _listing;

    /// <summary>What every NPU is used for, today.</summary>
    public const string UsedFor = "Detected, not used yet: Auto-Captions on the NPU need a speech model built for it (planned with the AI worker in 6.0).";

    /// <summary>The NPUs, listed once on a pool thread (the same task for every caller).</summary>
    public static Task<IReadOnlyList<NpuAdapter>> ListAsync()
    {
        lock (Gate)
            return _listing ??= Task.Run(Enumerate);
    }

    /// <summary>The NPUs, listed now on the calling thread (the benchmark's own process; never the UI thread).</summary>
    public static IReadOnlyList<NpuAdapter> Enumerate()
    {
        try
        {
            return DxCore.Npus().Select(n => new NpuAdapter(
                n.Description.Length > 0 ? n.Description : "NPU", VendorName(n.VendorId), n.VendorId, n.DeviceId, n.DriverVersionText,
                Math.Max(n.SharedSystemMemory, 0) / (1024 * 1024), n.GenericMl)).ToList();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or System.Runtime.InteropServices.SEHException)
        {
            AppLog.Write("The NPUs could not be listed", ex);
            return [];
        }
    }

    /// <summary>The vendor of a PCI or ACPI vendor number, as the hardware benchmark names them.</summary>
    public static string VendorName(uint vendorId) => vendorId switch
    {
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x10DE => "NVIDIA",
        0x5143 or 0x4D4F4351 => "Qualcomm",
        0x1414 => "Microsoft",
        _ => string.Create(CultureInfo.InvariantCulture, $"0x{vendorId:X4}"),
    };

    /// <summary>One line for an NPU: its name and its driver.</summary>
    public static string Describe(NpuAdapter npu) =>
        npu.DriverVersion.Length > 0 ? $"{npu.Name} (driver {npu.DriverVersion})" : npu.Name;
}
