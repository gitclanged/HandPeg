using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HandPegBootstrap;

/// <summary>Microsoft's installer of one .NET release's Windows Desktop Runtime for one processor.</summary>
internal sealed record DotNetInstaller(string Version, DotNetFile File);

/// <summary>
/// The .NET 10 Desktop Runtime installed for every program on the PC (Pass 5.3-Stage1, the choice dialog's second option): Microsoft's own
/// installer of the newest released version for this processor (windowsdesktop-runtime-&lt;version&gt;-win-&lt;arch&gt;.exe, which brings the
/// matching .NET Runtime with it), downloaded over HTTPS into a folder of its own, its SHA-512 checked against Microsoft's as it arrives and
/// again through a handle that keeps it from being changed until Windows has started it, then run with administrator rights (Windows asks:
/// "runas") as "/install /quiet /norestart", and waited for. Its exit code is the answer (<see cref="Launch.OutcomeOf"/>). An x64 copy on an
/// ARM64 PC installs the x64 runtime, which goes into Program Files\dotnet\x64, where <see cref="DotNetRoots"/> looks.
/// </summary>
internal static unsafe class SystemDotNet
{
    /// <summary>What Microsoft's installer is told: install, show nothing, never restart Windows.</summary>
    public const string InstallerArguments = "/install /quiet /norestart";

    /// <summary>
    /// The newest released version's (never a preview's) Windows Desktop Runtime installer for <paramref name="rid"/>: the file named
    /// windowsdesktop-runtime-&lt;rid&gt;.exe for that rid, from an https:// address with a SHA-512; null when the list has none.
    /// Shared with the golden tests (portable-bootstrap).
    /// </summary>
    public static DotNetInstaller? PickInstaller(JsonDocument list, string rid)
    {
        var wanted = $"windowsdesktop-runtime-{rid}.exe";
        foreach (var (release, name) in PortableDotNet.Released(list))
        {
            if (PortableDotNet.FileOf(release, "windowsdesktop", rid, n => n.Equals(wanted, StringComparison.Ordinal)) is { } file)
                return new DotNetInstaller(name, file);
        }

        return null;
    }

    /// <summary>
    /// Downloads Microsoft's installer for <paramref name="arch"/> and runs it with administrator rights, the UAC prompt owned by
    /// <paramref name="owner"/> (the progress window, so Windows asks in front); the installer's exit code, or
    /// <see cref="Launch.ErrorCancelled"/> when the UAC prompt was declined. Throws with a message for the user when it could not be
    /// downloaded or started, and OperationCanceledException when <paramref name="cancel"/> stops the wait (the installer goes on by itself).
    /// </summary>
    public static int Install(string arch, IntPtr owner, PortableDotNet.Progress progress, CancellationToken cancel)
    {
        var rid = $"win-{arch}";
        progress($"Asking Microsoft for the newest .NET {DotNetRoots.Major}...", 0);
        var installer = PortableDotNet.FromReleaseList<DotNetInstaller>(list => PickInstaller(list, rid),
            $"Microsoft lists no .NET {DotNetRoots.Major} Desktop Runtime installer for {rid}.", cancel);

        // A folder of its own, made fresh: nothing else is in it, and nothing is run from a folder an earlier attempt left.
        var folder = Path.Combine(Path.GetTempPath(), "HandPeg-dotnet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, $"windowsdesktop-runtime-{installer.Version}-{rid}.exe");
            Web.Download(installer.File.Url, path, installer.File.Sha512, cancel, (received, total) =>
                progress($"Downloading Microsoft's .NET {installer.Version} installer ({arch}): {received / 1048576.0:0.0} of {(total > 0 ? $"{total / 1048576.0:0.0}" : "?")} MB",
                    (total > 0 ? Math.Min((double)received / total, 1) : 0) * 0.6));
            cancel.ThrowIfCancellationRequested();

            IntPtr process;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Checked again through this handle, which lets nothing write to the file until the installer has started from it.
                if (!Web.Sha512Of(held).Equals(installer.File.Sha512, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"{Path.GetFileName(path)} changed after it was downloaded: it was not run.");

                progress("Windows asks for administrator rights to install .NET for every program...", 0.62);
                process = Start(path, folder, owner, out var error);
                if (process == IntPtr.Zero)
                {
                    if (error == Launch.ErrorCancelled)
                        return Launch.ErrorCancelled;
                    throw new IOException($"Windows could not start Microsoft's installer (Windows error {error}).");
                }
            }

            try
            {
                progress($"Microsoft's installer is putting .NET {installer.Version} on this PC...", 0.7);
                uint waited;
                while ((waited = Native.WaitForSingleObject(process, 250)) == Native.WaitTimeout)
                    cancel.ThrowIfCancellationRequested();
                if (waited != Native.WaitObject0 || !Native.GetExitCodeProcess(process, out var exit))
                    throw new IOException($"HandPeg could not wait for Microsoft's installer (Windows error {Marshal.GetLastPInvokeError()}).");

                progress($"Microsoft's installer has finished (code {exit}).", 1);
                return unchecked((int)exit);
            }
            finally
            {
                Native.CloseHandle(process);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use (the wait was stopped while the installer runs): left in the temporary folder, which Windows cleans.
            }
        }
    }

    /// <summary>Starts <paramref name="path"/> with administrator rights; its process handle, or zero with the Windows error (1223: the UAC prompt was declined).</summary>
    private static IntPtr Start(string path, string folder, IntPtr owner, out int error)
    {
        // The shell may hand a verb to COM objects, which want a single-threaded apartment: this thread's own, unless it has one already.
        var com = Native.CoInitializeEx(IntPtr.Zero, Native.CoinitApartmentThreaded | Native.CoinitDisableOle1Dde);
        try
        {
            fixed (char* verb = "runas", file = path, parameters = InstallerArguments, directory = folder)
            {
                var info = new Native.ShellExecuteInfo
                {
                    Size = (uint)sizeof(Native.ShellExecuteInfo),
                    Mask = Native.SeeMaskNoCloseProcess | Native.SeeMaskNoAsync | Native.SeeMaskFlagNoUi | Native.SeeMaskNoZoneChecks,
                    Window = owner,
                    Verb = verb,
                    FileName = file,
                    Parameters = parameters,
                    Folder = directory,
                    Show = Native.SwShowNormal,
                };
                if (!Native.ShellExecuteEx(ref info))
                {
                    error = Marshal.GetLastPInvokeError();
                    return IntPtr.Zero;
                }

                error = info.Process == IntPtr.Zero ? 2 : 0; // ERROR_FILE_NOT_FOUND: Windows said it started something but gave no process.
                return info.Process;
            }
        }
        finally
        {
            if (com >= 0)
                Native.CoUninitialize();
        }
    }
}
