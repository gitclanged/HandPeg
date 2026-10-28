using System.IO;
using System.Runtime.InteropServices;

namespace HandPegApp.Services;

/// <summary>What background work asks before it starts something long: is the machine on battery, is a folder across the network.</summary>
public static partial class PowerStatus
{
    /// <summary>Whether the machine runs on its battery now (a laptop unplugged). False when Windows cannot say.</summary>
    public static bool OnBattery => GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

    /// <summary>Whether a folder is on a network drive or share. Asked once per folder, away from the UI thread where it can be.</summary>
    public static bool IsNetworkFolder(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder)) ?? "";
            return root.StartsWith(@"\\", StringComparison.Ordinal) || (root.Length > 0 && new DriveInfo(root).DriveType == DriveType.Network);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}
