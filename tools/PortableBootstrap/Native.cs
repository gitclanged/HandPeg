using System.Runtime.InteropServices;

namespace HandPegBootstrap;

/// <summary>
/// Windows' own: the windows and their drawing, WinHTTP, BCrypt's SHA-512, the registry, the DLL loader, and starting Microsoft's installer
/// with administrator rights (LibraryImport: no marshalling made at run time).
/// </summary>
internal static unsafe partial class Native
{
    // ----- kernel32 -----

    public const uint LoadLibrarySearchDllLoadDir = 0x00000100, LoadLibrarySearchDefaultDirs = 0x00001000;

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GetProcAddress(IntPtr module, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FreeLibrary(IntPtr module);

    /// <summary>A DLL loaded in this process, by its file name ("hostpolicy.dll"); zero when none is. No reference is taken.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandleByName(string name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GetCurrentProcess();

    public const ushort ImageFileMachineArm64 = 0xAA64;

    public const uint WaitObject0 = 0x00000000, WaitTimeout = 0x00000102;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    // ----- shell32 / ole32: Microsoft's .NET installer started with administrator rights (Windows asks) -----

    public const uint SeeMaskNoCloseProcess = 0x00000040, SeeMaskNoAsync = 0x00000100, SeeMaskFlagNoUi = 0x00000400, SeeMaskNoZoneChecks = 0x00800000;
    public const uint CoinitApartmentThreaded = 0x2, CoinitDisableOle1Dde = 0x4;
    public const int SwShowNormal = 1;

    /// <summary>SHELLEXECUTEINFOW: what to start and how, and the started process's handle back.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ShellExecuteInfo
    {
        public uint Size, Mask;
        public IntPtr Window;
        public char* Verb, FileName, Parameters, Folder;
        public int Show;
        public IntPtr InstApp, IdList;
        public char* Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr IconOrMonitor, Process;
    }

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShellExecuteEx(ref ShellExecuteInfo info);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(IntPtr reserved, uint flags);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    // ----- advapi32: where .NET's installer says it put .NET (the 32-bit view, as .NET's own host reads it) -----

    private const uint RrfRtRegSz = 0x00000002, RrfSubkeyWow6432Key = 0x00020000;
    private static readonly IntPtr HkeyLocalMachine = unchecked((IntPtr)(int)0x80000002);

    [LibraryImport("advapi32.dll", EntryPoint = "RegGetValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegGetValue(IntPtr key, string subKey, string value, uint flags, IntPtr type, char* data, ref uint bytes);

    /// <summary>HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\&lt;arch&gt;\InstallLocation; null when there is none.</summary>
    public static string? RegisteredDotNet(string arch)
    {
        var buffer = stackalloc char[1024];
        var bytes = 1024u * 2;
        return RegGetValue(HkeyLocalMachine, $@"SOFTWARE\dotnet\Setup\InstalledVersions\{arch}", "InstallLocation", RrfRtRegSz | RrfSubkeyWow6432Key, IntPtr.Zero, buffer, ref bytes) == 0
            ? new string(buffer).TrimEnd('\0') : null;
    }

    // ----- user32 / gdi32: the download window and the choice dialog -----

    public const uint WsOverlapped = 0x00000000, WsCaption = 0x00C00000, WsSysMenu = 0x00080000, WsMinimizeBox = 0x00020000, WsVisible = 0x10000000;
    public const uint WsChild = 0x40000000, WsTabStop = 0x00010000, WsClipChildren = 0x02000000, BsOwnerDraw = 0x0000000B;
    public const int CwUseDefault = unchecked((int)0x80000000);
    public const uint WmDestroy = 0x0002, WmPaint = 0x000F, WmClose = 0x0010, WmEraseBkgnd = 0x0014, WmDpiChanged = 0x02E0, WmApp = 0x8000;
    public const uint WmSize = 0x0005, WmActivate = 0x0006, WmSetFocus = 0x0007, WmDrawItem = 0x002B, WmCommand = 0x0111, WmCtlColorBtn = 0x0135;

    /// <summary>DM_GETDEFID: IsDialogMessage asks the window which button Enter presses; answered as MAKELONG(id, DC_HASDEFID).</summary>
    public const uint DmGetDefId = 0x0400;
    public const int DcHasDefId = 0x534B;
    public const uint OdsSelected = 0x0001, OdsFocus = 0x0010, OdsNoFocusRect = 0x0200;
    public const uint MbOk = 0x0, MbIconError = 0x10, MbIconWarning = 0x30, MbSetForeground = 0x10000;
    public const int SwShow = 5;
    public const uint DtLeft = 0x0, DtCenter = 0x1, DtSingleLine = 0x20, DtVCenter = 0x4, DtEndEllipsis = 0x8000, DtNoPrefix = 0x800, DtWordBreak = 0x10, DtEditControl = 0x2000;
    public const int Transparent = 1, NullBrush = 5, PsSolid = 0;
    public static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = -4;

    /// <summary>DRAWITEMSTRUCT: an owner-drawn button to paint, its state (ODS_*) and where.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DrawItemStruct
    {
        public uint ControlType, ControlId, ItemId, ItemAction, ItemState;
        public IntPtr Item, Dc;
        public Rect Bounds;
        public nuint ItemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaintStruct
    {
        public IntPtr Dc;
        public int Erase;
        public Rect Paint;
        public int Restore, IncUpdate;
        public fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassEx
    {
        public uint Size, Style;
        public delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr> WndProc;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public char* MenuName, ClassName;
        public IntPtr IconSmall;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassEx(in WndClassEx windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out Msg message, IntPtr window, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in Msg message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessage(in Msg message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(IntPtr window, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32.dll")]
    public static partial IntPtr BeginPaint(IntPtr window, out PaintStruct paint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(IntPtr window, in PaintStruct paint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr window, out Rect rect);

    [LibraryImport("user32.dll")]
    public static partial int FillRect(IntPtr dc, in Rect rect, IntPtr brush);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawText(IntPtr dc, string text, int length, ref Rect rect, uint format);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(IntPtr owner, string text, string caption, uint type);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial IntPtr LoadCursor(IntPtr instance, IntPtr name);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW")]
    public static partial IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfoW(uint action, uint param, out Rect rect, uint winIni);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustWindowRectExForDpi(ref Rect rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle, uint dpi);

    [LibraryImport("user32.dll", EntryPoint = "IsDialogMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsDialogMessage(IntPtr dialog, in Msg message);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetFocus(IntPtr window);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetFocus();

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreatePen(int style, int width, int color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RoundRect(IntPtr dc, int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr GetStockObject(int index);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateSolidBrush(int color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr gdiObject);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [LibraryImport("gdi32.dll")]
    public static partial int SetTextColor(IntPtr dc, int color);

    [LibraryImport("gdi32.dll")]
    public static partial int SetBkMode(IntPtr dc, int mode);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateCompatibleDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(IntPtr dc, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr dc);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    public static partial IntPtr GetModuleHandle(IntPtr name);

    // ----- winhttp: Microsoft's metadata and zips, over HTTPS, through the PC's proxy -----

    public const uint WinHttpAccessTypeAutomaticProxy = 4, WinHttpAccessTypeDefaultProxy = 0, WinHttpFlagSecure = 0x00800000;
    public const uint WinHttpQueryStatusCode = 19, WinHttpQueryContentLength = 5, WinHttpQueryFlagNumber = 0x20000000, WinHttpQueryFlagNumber64 = 0x08000000;
    public const int InternetSchemeHttps = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct UrlComponents
    {
        public uint Size;
        public char* Scheme;
        public uint SchemeLength;
        public int SchemeNumber;
        public char* HostName;
        public uint HostNameLength;
        public ushort Port;
        public char* UserName;
        public uint UserNameLength;
        public char* Password;
        public uint PasswordLength;
        public char* UrlPath;
        public uint UrlPathLength;
        public char* ExtraInfo;
        public uint ExtraInfoLength;
    }

    [LibraryImport("winhttp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr WinHttpOpen(string agent, uint accessType, IntPtr proxy, IntPtr bypass, uint flags);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpCrackUrl(char* url, uint length, uint flags, ref UrlComponents components);

    [LibraryImport("winhttp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr WinHttpConnect(IntPtr session, string server, ushort port, uint reserved);

    [LibraryImport("winhttp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr WinHttpOpenRequest(IntPtr connect, string verb, string path, IntPtr version, IntPtr referrer, IntPtr acceptTypes, uint flags);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpSendRequest(IntPtr request, IntPtr headers, uint headersLength, IntPtr optional, uint optionalLength, uint totalLength, IntPtr context);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpReceiveResponse(IntPtr request, IntPtr reserved);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpQueryHeaders(IntPtr request, uint level, IntPtr name, void* buffer, ref uint length, IntPtr index);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpReadData(IntPtr request, byte* buffer, uint toRead, out uint read);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpSetTimeouts(IntPtr handle, int resolve, int connect, int send, int receive);

    [LibraryImport("winhttp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinHttpCloseHandle(IntPtr handle);

    // ----- bcrypt: SHA-512 as the bytes arrive -----

    [LibraryImport("bcrypt.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string id, IntPtr implementation, uint flags);

    [LibraryImport("bcrypt.dll")]
    public static partial int BCryptCreateHash(IntPtr algorithm, out IntPtr hash, IntPtr hashObject, uint hashObjectLength, IntPtr secret, uint secretLength, uint flags);

    [LibraryImport("bcrypt.dll")]
    public static partial int BCryptHashData(IntPtr hash, byte* input, uint length, uint flags);

    [LibraryImport("bcrypt.dll")]
    public static partial int BCryptFinishHash(IntPtr hash, byte* output, uint length, uint flags);

    [LibraryImport("bcrypt.dll")]
    public static partial int BCryptDestroyHash(IntPtr hash);

    [LibraryImport("bcrypt.dll")]
    public static partial int BCryptCloseAlgorithmProvider(IntPtr algorithm, uint flags);
}
