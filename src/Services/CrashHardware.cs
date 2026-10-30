using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using Microsoft.Win32;

namespace HandPegApp.Services;

/// <summary>
/// What the computer is, for a crash report's environment (Pass 4.1B): the processor's model and its cores, the memory
/// and its speed, each card's memory, the main display, the power plan, the theme and the engines' versions. Asked of
/// Windows directly, once, after the first frame (<see cref="CrashReporter"/>): CPUID, GetLogicalProcessorInformationEx,
/// the raw SMBIOS tables (GetSystemFirmwareTable), EnumDisplaySettings, the power plan's GUID. No WMI, well under a
/// millisecond. Only numbers and model names are read: never a serial number, an asset tag, a part number, the
/// computer's name or a power plan's own name (a plan Windows does not ship is "custom").
///
/// The sentinel learns them through the shared memory: <see cref="Pack"/> puts them in four "env." context slots and
/// <see cref="Unpack"/> reads them back, so the report it writes says the same as one HandPeg writes itself.
/// </summary>
public static partial class CrashHardware
{
    /// <summary>The processor as it names itself ("AMD Ryzen 9 7950X 16-Core Processor"); null when it does not say.</summary>
    public static string? Cpu { get; private set; }

    /// <summary>Its cores (fewer than its logical processors when SMT / Hyper-Threading is on); 0 when not known.</summary>
    public static int PhysicalCores { get; private set; }

    /// <summary>The memory installed, in GB (the modules SMBIOS lists, else what Windows says is installed); 0 when not known.</summary>
    public static double RamGb { get; private set; }

    /// <summary>The speed the memory runs at, in MT/s (SMBIOS's configured speed); 0 when not known.</summary>
    public static int RamMts { get; private set; }

    /// <summary>The modules' own rated speed, in MT/s: above <see cref="RamMts"/> when XMP / EXPO is off; 0 when not known.</summary>
    public static int RamRatedMts { get; private set; }

    /// <summary>The main display's mode: its size in pixels and its refresh rate; and the system's scale, in percent.</summary>
    public static int DisplayWidth { get; private set; }
    public static int DisplayHeight { get; private set; }
    public static int DisplayHz { get; private set; }
    public static int DisplayScalePercent { get; private set; }

    /// <summary>"Balanced, Best performance, on AC": the power plan by its well-known GUID, Windows' power mode, the power source and battery saver.</summary>
    public static string? PowerPlan { get; private set; }

    private static int _learnt;

    /// <summary>Learns everything once; later calls return at once. Never throws.</summary>
    public static void Learn()
    {
        if (Interlocked.Exchange(ref _learnt, 1) != 0)
            return;

        Try(() => Cpu = ProcessorName());
        Try(() => PhysicalCores = CountCores());
        Try(ReadMemory);
        Try(ReadDisplay);
        Try(() => PowerPlan = DescribePower());
    }

    private static void Try(Action learn)
    {
        try
        {
            learn();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException
                                       or System.Security.SecurityException or UnauthorizedAccessException or IOException
                                       or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            // What could not be learnt is left out of the report.
        }
    }

    // ----- The processor -----

    /// <summary>The brand string CPUID gives (leaves 0x80000002 to 4); on a processor without CPUID, the one Windows keeps.</summary>
    private static string? ProcessorName()
    {
        if (X86Base.IsSupported && (uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax >= 0x80000004)
        {
            var bytes = new byte[48];
            for (var leaf = 0; leaf < 3; leaf++)
            {
                var (a, b, c, d) = X86Base.CpuId(unchecked((int)0x80000002) + leaf, 0);
                foreach (var (value, at) in new[] { (a, 0), (b, 4), (c, 8), (d, 12) })
                    BitConverter.TryWriteBytes(bytes.AsSpan(leaf * 16 + at, 4), value);
            }

            var name = Encoding.ASCII.GetString(bytes).TrimEnd('\0').Trim();
            if (name.Length > 0)
                return CollapseSpaces(name);
        }

        // ARM64 has no CPUID. Read only: nothing is written, so a self-contained copy leaves no trace (AppPaths).
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return key?.GetValue("ProcessorNameString") is string named && named.Trim().Length > 0 ? CollapseSpaces(named.Trim()) : null;
    }

    private static string CollapseSpaces(string text) => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private const int RelationProcessorCore = 0;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>The cores: one SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX of RelationProcessorCore each.</summary>
    private static unsafe int CountCores()
    {
        uint length = 0;
        if (GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref length) != 0 || Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer || length == 0)
            return 0;

        var buffer = new byte[length];
        fixed (byte* start = buffer)
        {
            if (GetLogicalProcessorInformationEx(RelationProcessorCore, start, ref length) == 0)
                return 0;

            var cores = 0;
            for (uint at = 0; at + 8 <= length;)
            {
                var size = *(uint*)(start + at + 4);
                if (size == 0)
                    break;
                if (*(int*)(start + at) == RelationProcessorCore)
                    cores++;
                at += size;
            }

            return cores;
        }
    }

    // ----- The memory (SMBIOS type 17, Memory Device) -----

    private const uint Rsmb = 0x52534D42; // 'RSMB'
    private const byte MemoryDevice = 17;
    private const byte EndOfTable = 127;

    /// <summary>
    /// Sums the memory devices SMBIOS lists and takes their speed: the size (0x0C, or the extended size at 0x1C), the
    /// rated speed (0x15, or 0x54) and the configured speed (0x20, or 0x58). Only these numbers are read: the
    /// structures' strings (maker, serial and part numbers) are skipped over unread.
    /// </summary>
    private static unsafe void ReadMemory()
    {
        var size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size > 8)
        {
            var buffer = new byte[size];
            fixed (byte* raw = buffer)
            {
                if (GetSystemFirmwareTable(Rsmb, 0, raw, size) == size)
                    ReadMemoryDevices(buffer.AsSpan(8, (int)Math.Min(BitConverter.ToUInt32(buffer, 4), size - 8)));
            }
        }

        if (RamGb <= 0 && GetPhysicallyInstalledSystemMemory(out var kilobytes) != 0 && kilobytes > 0)
            RamGb = Math.Round(kilobytes / (1024.0 * 1024.0), 1);
    }

    /// <summary>Reads the memory devices out of an SMBIOS table (exposed for the tests).</summary>
    public static void ReadMemoryDevices(ReadOnlySpan<byte> table)
    {
        long megabytes = 0;
        int configured = 0, rated = 0;
        for (var at = 0; at + 4 <= table.Length;)
        {
            var type = table[at];
            var length = table[at + 1];
            if (length < 4 || at + length > table.Length)
                break;

            if (type == MemoryDevice && length >= 0x17)
            {
                var device = table.Slice(at, length);
                var sizeField = Word(device, 0x0C);
                long mb = sizeField switch
                {
                    0 or 0xFFFF => 0,
                    0x7FFF when length >= 0x20 => Dword(device, 0x1C) & 0x7FFFFFFF,
                    _ when (sizeField & 0x8000) != 0 => (sizeField & 0x7FFF) / 1024,
                    _ => sizeField,
                };
                if (mb > 0)
                {
                    megabytes += mb;
                    var speed = Speed(device, 0x15, 0x54);
                    var running = length >= 0x22 ? Speed(device, 0x20, 0x58) : 0;
                    rated = Math.Max(rated, speed);
                    configured = Math.Max(configured, running);
                }
            }

            if (type == EndOfTable)
                break;

            // The strings follow the formatted part, each ended by a zero, the set by another.
            var strings = at + length;
            while (strings + 1 < table.Length && (table[strings] != 0 || table[strings + 1] != 0))
                strings++;
            at = strings + 2;
        }

        if (megabytes > 0)
        {
            RamGb = Math.Round(megabytes / 1024.0, 1);
            RamRatedMts = rated;
            RamMts = configured > 0 ? configured : rated;
        }
    }

    // A speed field: 0 unknown; 0xFFFF means the extended DWORD (SMBIOS 3.3) holds it.
    private static int Speed(ReadOnlySpan<byte> device, int field, int extended)
    {
        if (device.Length < field + 2)
            return 0;
        var speed = Word(device, field);
        return speed == 0xFFFF ? device.Length >= extended + 4 ? (int)(Dword(device, extended) & 0x7FFFFFFF) : 0 : speed;
    }

    private static ushort Word(ReadOnlySpan<byte> data, int at) => (ushort)(data[at] | data[at + 1] << 8);

    private static uint Dword(ReadOnlySpan<byte> data, int at) => (uint)(data[at] | data[at + 1] << 8 | data[at + 2] << 16 | data[at + 3] << 24);

    // ----- The display -----

    private const int EnumCurrentSettings = -1;
    private const int DevModeSize = 220;

    /// <summary>The main display's mode (DEVMODEW: dmPelsWidth at 172, dmPelsHeight at 176, dmDisplayFrequency at 184) and the system's DPI.</summary>
    private static unsafe void ReadDisplay()
    {
        var mode = stackalloc byte[DevModeSize];
        new Span<byte>(mode, DevModeSize).Clear();
        *(ushort*)(mode + 68) = DevModeSize;
        if (EnumDisplaySettingsW(null, EnumCurrentSettings, mode) != 0)
        {
            DisplayWidth = *(int*)(mode + 172);
            DisplayHeight = *(int*)(mode + 176);
            DisplayHz = *(int*)(mode + 184);
        }

        var dpi = GetDpiForSystem();
        DisplayScalePercent = dpi > 0 ? (int)Math.Round(dpi * 100.0 / 96) : 0;
    }

    // ----- Power -----

    private static readonly Dictionary<Guid, string> Plans = new()
    {
        [new Guid("381b4222-f694-41f0-9685-ff5bb260df2e")] = "Balanced",
        [new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")] = "High performance",
        [new Guid("a1841308-3541-4fab-bc81-f71556f20b4a")] = "Power saver",
        [new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61")] = "Ultimate Performance",
    };

    // Windows 10 and 11's power mode, laid over the plan.
    private static readonly Dictionary<Guid, string> Modes = new()
    {
        [new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a")] = "Best power efficiency",
        [Guid.Empty] = "Balanced mode",
        [new Guid("3af9b8d9-7c97-431d-ad78-34a8bfea439f")] = "Better performance",
        [new Guid("ded574b5-45a0-4f42-8737-46345c09c238")] = "Best performance",
    };

    private static unsafe string? DescribePower()
    {
        var parts = new List<string>();
        if (PowerGetActiveScheme(0, out var scheme) == 0 && scheme != 0)
        {
            try
            {
                parts.Add(Plans.TryGetValue(*(Guid*)scheme, out var plan) ? plan : "custom plan");
            }
            finally
            {
                LocalFree(scheme);
            }
        }

        try
        {
            if (PowerGetEffectiveOverlayScheme(out var overlay) == 0 && Modes.TryGetValue(overlay, out var mode))
                parts.Add(mode);
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows has no power mode.
        }

        if (GetSystemPowerStatus(out var status) != 0)
        {
            if (status.ACLineStatus is 0 or 1)
                parts.Add(status.ACLineStatus == 1 ? "on AC" : "on battery");
            if (status.SystemStatusFlag == 1)
                parts.Add("battery saver");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    // ----- Into the report, and through the shared memory -----

    /// <summary>The engines' versions (whisper.cpp, yt-dlp), learnt beside FFmpeg's.</summary>
    public static string? Whisper { get; set; }
    public static string? YtDlp { get; set; }

    /// <summary>Fills a report's environment with what was learnt (and the cards, theme and Efficiency mode passed in).</summary>
    public static void Fill(CrashEnvironment environment, IEnumerable<GpuAdapter> cards, string? theme, bool? efficiencyMode)
    {
        environment.Cpu = Clean(Cpu, 100);
        environment.PhysicalCores = PhysicalCores > 0 ? PhysicalCores : null;
        environment.RamGb = RamGb > 0 ? RamGb : null;
        environment.RamMts = RamMts > 0 ? RamMts : null;
        environment.RamRatedMts = RamRatedMts > 0 ? RamRatedMts : null;
        environment.Adapters = cards.Select(c => new CrashGpu
        {
            Name = CrashSanitizer.Sanitize(c.Name, 120),
            VendorId = string.Create(CultureInfo.InvariantCulture, $"0x{c.VendorId:X4}"),
            DeviceId = string.Create(CultureInfo.InvariantCulture, $"0x{c.DeviceId:X4}"),
            Driver = c.DriverVersion.Length > 0 ? CrashSanitizer.Sanitize(c.DriverVersion, 40) : null,
            VramMb = c.DedicatedVideoMemoryMb,
        }).ToList() is { Count: > 0 } adapters ? adapters : null;
        environment.Display = DisplayWidth > 0
            ? new CrashDisplay { Width = DisplayWidth, Height = DisplayHeight, RefreshHz = DisplayHz, ScalePercent = DisplayScalePercent }
            : null;
        environment.PowerPlan = Clean(PowerPlan, 100);
        environment.Theme = Clean(theme, 40);
        environment.EfficiencyMode = efficiencyMode;
        environment.Whisper = Clean(Whisper, 60);
        environment.YtDlp = Clean(YtDlp, 60);
    }

    private static string? Clean(string? text, int max) => string.IsNullOrWhiteSpace(text) ? null : CrashSanitizer.Sanitize(text, max);

    // The four slots (232 bytes each). Fields are separated by '|', cards by ';' with their fields by ':' (the name last,
    // so a ':' in it survives). Model names are cut to fit; nothing else here is long.
    public const string CpuSlot = "env.cpu";
    public const string AdaptersSlot = "env.adapters";
    public const string SystemSlot = "env.system";
    public const string EnginesSlot = "env.engines";

    /// <summary>What the sentinel is handed: the four slots' values, by key.</summary>
    public static IEnumerable<(string Key, string Value)> Pack(IEnumerable<GpuAdapter> cards, string? theme, bool efficiencyMode)
    {
        var invariant = CultureInfo.InvariantCulture;
        yield return (CpuSlot, string.Create(invariant, $"{PhysicalCores}|{RamGb}|{RamMts}|{RamRatedMts}|{Cut(Cpu, 120)}"));
        yield return (AdaptersSlot, string.Join(";", cards.Select(c =>
            string.Create(invariant, $"{c.VendorId:X4}:{c.DeviceId:X4}:{c.DedicatedVideoMemoryMb}:{c.DriverVersion}:{Cut(c.Name.Replace(";", ","), 60)}"))));
        yield return (SystemSlot, string.Create(invariant,
            $"{DisplayWidth}|{DisplayHeight}|{DisplayHz}|{DisplayScalePercent}|{(efficiencyMode ? 1 : 0)}|{Cut(theme, 30)}|{Cut(PowerPlan, 90)}"));
        yield return (EnginesSlot, $"{Cut(Whisper, 60)}|{Cut(YtDlp, 60)}");
    }

    private static string Cut(string? text, int max) => text is null ? "" : (text.Length > max ? text[..max] : text).Replace("|", "/");

    /// <summary>Reads the four slots back into a report's environment (the sentinel). What is missing stays null.</summary>
    public static void Unpack(IReadOnlyDictionary<string, string> context, CrashEnvironment environment)
    {
        static string? Text(string[] fields, int at) => at < fields.Length && fields[at].Trim().Length > 0 ? fields[at].Trim() : null;
        static int Number(string[] fields, int at) => at < fields.Length && int.TryParse(fields[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        if (context.TryGetValue(CpuSlot, out var cpu))
        {
            var f = cpu.Split('|', 5);
            var cores = Number(f, 0);
            environment.PhysicalCores = cores > 0 ? cores : null;
            environment.RamGb = f.Length > 1 && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) && gb > 0 ? gb : null;
            environment.RamMts = Number(f, 2) is > 0 and var mts ? mts : null;
            environment.RamRatedMts = Number(f, 3) is > 0 and var rated ? rated : null;
            environment.Cpu = Clean(Text(f, 4), 100);
        }

        if (context.TryGetValue(AdaptersSlot, out var adapters) && adapters.Length > 0)
        {
            var cards = new List<CrashGpu>();
            foreach (var card in adapters.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = card.Split(':', 5);
                if (f.Length < 5)
                    continue;
                cards.Add(new CrashGpu
                {
                    VendorId = "0x" + f[0],
                    DeviceId = "0x" + f[1],
                    VramMb = long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vram) ? vram : 0,
                    Driver = Clean(f[3], 40),
                    Name = CrashSanitizer.Sanitize(f[4], 120),
                });
            }

            environment.Adapters = cards.Count > 0 ? cards : null;
            environment.Gpus = cards.Select(c => c.Driver is null ? c.Name : $"{c.Name} (driver {c.Driver})").ToList();
        }

        if (context.TryGetValue(SystemSlot, out var system))
        {
            var f = system.Split('|', 7);
            environment.Display = Number(f, 0) > 0
                ? new CrashDisplay { Width = Number(f, 0), Height = Number(f, 1), RefreshHz = Number(f, 2), ScalePercent = Number(f, 3) }
                : null;
            environment.EfficiencyMode = f.Length > 4 ? f[4] == "1" : null;
            environment.Theme = Clean(Text(f, 5), 40);
            environment.PowerPlan = Clean(Text(f, 6), 100);
        }

        if (context.TryGetValue(EnginesSlot, out var engines))
        {
            var f = engines.Split('|', 2);
            environment.Whisper = Clean(Text(f, 0), 60);
            environment.YtDlp = Clean(Text(f, 1), 60);
        }
    }

    // ----- Windows -----

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint GetSystemFirmwareTable(uint provider, uint table, byte* buffer, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial int GetPhysicallyInstalledSystemMemory(out ulong kilobytes);

    [LibraryImport("user32.dll")]
    private static unsafe partial int EnumDisplaySettingsW(char* device, int mode, byte* devMode);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetSystemPowerStatus(out SystemPowerStatus status);
}
