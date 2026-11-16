using System.Diagnostics;

namespace HandPegApp.Services;

/// <summary>
/// Opens a document, a folder or an address the way Explorer does (ShellExecute), disposing the process object it returns (or none,
/// when Windows hands the target to a process already running). Failures (<see cref="System.ComponentModel.Win32Exception"/>,
/// <see cref="InvalidOperationException"/>) are left to the caller, which shows them its own way.
/// </summary>
public static class ShellOpen
{
    /// <summary>Opens <paramref name="target"/> (a file, a folder, an http(s) address, or a program with <paramref name="arguments"/>).</summary>
    public static void Open(string target, string? arguments = null)
    {
        using var started = Process.Start(new ProcessStartInfo(target, arguments ?? "") { UseShellExecute = true });
    }

    /// <summary>Opens Explorer on the folder of <paramref name="path"/> with the file selected.</summary>
    public static void Reveal(string path) => Open("explorer.exe", $"/select,\"{path}\"");

    /// <summary>Opens Explorer on <paramref name="folder"/>.</summary>
    public static void OpenFolder(string folder) => Open("explorer.exe", $"\"{folder}\"");
}
