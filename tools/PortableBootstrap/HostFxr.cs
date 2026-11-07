using System.Runtime.InteropServices;

namespace HandPegBootstrap;

/// <summary>
/// HandPeg.dll run in this process (Pass 5.2.1), as .NET's own apphost runs it: the root's hostfxr.dll loaded, initialised for the
/// command line "HandPeg.dll &lt;arguments&gt;" with this program as the host and the root as dotnet_root, and run on this thread, which
/// is the STA thread WPF needs (Program.Main is [STAThread]). What hostfxr writes of an error is kept for the message.
/// </summary>
internal static unsafe class HostFxr
{
    /// <summary>hostfxr's "a framework it needs is missing" (FrameworkMissingFailure).</summary>
    public const int FrameworkMissing = unchecked((int)0x80008096);

    [StructLayout(LayoutKind.Sequential)]
    private struct InitializeParameters
    {
        public nuint Size;
        public char* HostPath;
        public char* DotNetRoot;
    }

    private static readonly System.Text.StringBuilder Errors = new();

    /// <summary>What hostfxr said of the last failure, if anything.</summary>
    public static string LastErrors => Errors.ToString().Trim();

    [UnmanagedCallersOnly]
    private static void WriteError(char* message)
    {
        if (message is not null)
            Errors.AppendLine(new string(message));
    }

    /// <summary>
    /// Runs the app; HandPeg's exit code. A failure before it runs (hostfxr missing or refusing the root) is a negative hostfxr status,
    /// or -1 when hostfxr.dll could not be loaded at all.
    /// </summary>
    public static int Run(DotNetRoot root, string hostPath, string appDll, string[] arguments, out bool started)
    {
        started = false;
        Errors.Clear();
        var library = Native.LoadLibraryEx(root.HostFxr, IntPtr.Zero, Native.LoadLibrarySearchDllLoadDir | Native.LoadLibrarySearchDefaultDirs);
        if (library == IntPtr.Zero)
        {
            Errors.AppendLine($"{root.HostFxr} could not be loaded (Windows error {Marshal.GetLastPInvokeError()}).");
            return -1;
        }

        var setErrorWriter = (delegate* unmanaged<delegate* unmanaged<char*, void>, IntPtr>)Native.GetProcAddress(library, "hostfxr_set_error_writer");
        var initialize = (delegate* unmanaged<int, char**, InitializeParameters*, IntPtr*, int>)Native.GetProcAddress(library, "hostfxr_initialize_for_dotnet_command_line");
        var run = (delegate* unmanaged<IntPtr, int>)Native.GetProcAddress(library, "hostfxr_run_app");
        var close = (delegate* unmanaged<IntPtr, int>)Native.GetProcAddress(library, "hostfxr_close");
        if (initialize is null || run is null || close is null)
        {
            Errors.AppendLine($"{root.HostFxr} is too old to run HandPeg.");
            return -1;
        }

        if (setErrorWriter is not null)
            setErrorWriter(&WriteError);

        // The command line as the dotnet executable would take it: the app first, then its own arguments.
        string[] line = [appDll, .. arguments];
        var pinned = new GCHandle[line.Length];
        var argv = stackalloc char*[line.Length];
        try
        {
            for (var i = 0; i < line.Length; i++)
            {
                pinned[i] = GCHandle.Alloc(line[i], GCHandleType.Pinned);
                argv[i] = (char*)pinned[i].AddrOfPinnedObject();
            }

            fixed (char* host = hostPath, dotnetRoot = root.Folder)
            {
                var parameters = new InitializeParameters { Size = (nuint)sizeof(InitializeParameters), HostPath = host, DotNetRoot = dotnetRoot };
                var context = IntPtr.Zero;
                var status = initialize(line.Length, argv, &parameters, &context);
                if (status < 0)
                {
                    if (context != IntPtr.Zero)
                        close(context);
                    return status;
                }

                started = true;
                var exit = run(context);
                close(context);
                return exit;
            }
        }
        finally
        {
            foreach (var handle in pinned)
            {
                if (handle.IsAllocated)
                    handle.Free();
            }
        }
    }
}
