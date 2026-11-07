using System.Runtime.InteropServices;

namespace HandPegBootstrap;

/// <summary>
/// HandPeg.dll run in this process (Pass 5.2.1), as .NET's own apphost runs it: the root's hostfxr.dll loaded, initialised for the
/// command line "HandPeg.dll &lt;arguments&gt;" with this program as the host and the root as dotnet_root, and run on this thread, which
/// is the STA thread WPF needs (Program.Main is [STAThread]). What hostfxr writes of an error is kept for the message. A root whose hostfxr
/// will not start HandPeg is unloaded again (Pass 5.3-Stage1), so that Program can go on to the next.
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
    /// or -1 when hostfxr.dll could not be loaded at all; then <paramref name="started"/> is false and nothing of that root is left loaded
    /// (<see cref="Unload"/>), so Program can try the next root in this process.
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

        try
        {
            return Start(library, root, hostPath, appDll, arguments, out started);
        }
        finally
        {
            if (!started)
                Unload(library);
        }
    }

    /// <summary>
    /// A hostfxr that did not start HandPeg unloaded, with the hostpolicy.dll it may have loaded from the same root, so that Program can try
    /// the next root's hostfxr in this process and a download can move this root aside. No runtime is left behind: hostfxr loads the
    /// runtime (coreclr.dll) only in hostfxr_run_app, which a start that was not started never reached (<see cref="Start"/> calls it only
    /// after a successful initialize), and a failed hostfxr_initialize_for_dotnet_command_line undoes its own start before it returns
    /// (hostpolicy's state unloaded, no host context left active), so neither DLL is in use.
    /// </summary>
    private static void Unload(IntPtr library)
    {
        Native.FreeLibrary(library);

        // hostfxr held the only reference to the hostpolicy.dll it loaded: nothing else in this process loads one before a runtime starts,
        // and an earlier refused root's was unloaded the same way.
        var policy = Native.GetModuleHandleByName("hostpolicy.dll");
        if (policy != IntPtr.Zero)
            Native.FreeLibrary(policy);
    }

    /// <summary>The loaded hostfxr initialised for HandPeg.dll and, once that succeeds, run (<paramref name="started"/> set just before).</summary>
    private static int Start(IntPtr library, DotNetRoot root, string hostPath, string appDll, string[] arguments, out bool started)
    {
        started = false;
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
