using System.Runtime.InteropServices;

namespace HandPegBootstrap;

/// <summary>What the user chose in the choice dialog.</summary>
internal enum DotNetChoice
{
    /// <summary>Cancel, Esc or the window closed: HandPeg does not start.</summary>
    Cancel,

    /// <summary>Download .NET 10 into this copy's own root (no admin).</summary>
    OwnCopy,

    /// <summary>Install .NET 10 for every program on the PC with Microsoft's installer (admin).</summary>
    SystemWide,
}

/// <summary>The dialog's words for one kind of copy: its heading, what it says, and the line under each of the two ways.</summary>
internal sealed record ChoiceText(string Heading, string Body, string OwnDetail, string SystemDetail);

/// <summary>
/// The dialog shown when no .NET 10 is found, or none found will start (Pass 5.3-Stage1, the user's spec), for every kind of copy, never
/// Windows' own "You must install .NET" box: dark in ProgressWindow's colours (a dark title bar), drawn with GDI (no WPF yet), at the
/// monitor's DPI (its thread per-monitor aware, put back after), with three real buttons (owner-drawn child BUTTONs: the manifest has no common controls 6, so a
/// plain button would draw in the classic style). Being real buttons, they keep keyboard focus, the Tab order and the names a screen
/// reader reads. IsDialogMessage gives the keys: Tab and Shift+Tab move between them, Space presses the focused one, Enter presses the
/// focused one (the recommended download when none has the focus: DM_GETDEFID is answered here), Esc and closing the window cancel.
/// <see cref="Show"/> returns once the window has gone.
/// </summary>
internal static unsafe class ChoiceWindow
{
    /// <summary>The recommended way: .NET 10 of HandPeg's own, no admin.</summary>
    public const string OwnCopyLabel = "Download .NET 10 for HandPeg Only (Recommended — Fast, No Admin)";

    /// <summary>The other way: Microsoft's installer, for every program on the PC.</summary>
    public const string SystemWideLabel = "Install .NET 10 System-Wide (Admin)";

    private const string CancelLabel = "Cancel";
    private const string ClassName = "HandPegDotNetChoice";

    // The buttons' control ids: IDOK and IDCANCEL are the ones IsDialogMessage presses for Enter (with no button focused) and Esc.
    private const int OwnCopyId = 1, CancelId = 2, SystemWideId = 100;

    // ProgressWindow's colours, and the buttons' own (COLORREFs: 0x00BBGGRR).
    private const int Background = 0x12_0F_0F, Track = 0x30_2A_2A, Text = 0xEE_EA_EA, Muted = 0xA6_A0_A0, Bar = 0xF6_82_3B;
    private const int BarPressed = 0xEB_63_25, TrackPressed = 0x3C_34_34, Edge = 0x42_3A_3A, White = 0xFF_FF_FF;

    // The layout in DIPs: the client area, its margin, and the buttons' places (each way's line of detail is under its button). The words
    // above the buttons have room for four lines: naming a .NET that could not start takes its folder's path.
    private const int Width = 560, Height = 366, Margin = 24, ButtonHeight = 38, OwnTop = 134, SystemTop = 222, CancelTop = 314, CancelWidth = 104, CancelHeight = 32;
    private const uint Style = Native.WsOverlapped | Native.WsCaption | Native.WsSysMenu | Native.WsClipChildren;
    private const uint NoZOrderNoActivate = 0x0014;

    private static IntPtr _own, _system, _cancel, _focus, _brush;
    private static IntPtr _headingFont, _bodyFont, _labelFont, _detailFont;
    private static DotNetChoice _choice;
    private static ChoiceText _text = new("", "", "", "");

    /// <summary>Shows the dialog with <paramref name="text"/> and waits for the user's choice.</summary>
    public static DotNetChoice Show(ChoiceText text)
    {
        (_text, _choice, _focus) = (text, DotNetChoice.Cancel, IntPtr.Zero);
        var previous = Native.SetThreadDpiAwarenessContext(Native.DpiAwarenessContextPerMonitorAwareV2);
        try
        {
            var window = Create();
            if (window == IntPtr.Zero)
                return DotNetChoice.Cancel;

            while (Native.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (Native.IsDialogMessage(window, message))
                    continue;

                Native.TranslateMessage(message);
                Native.DispatchMessage(message);
            }
        }
        finally
        {
            DeleteFonts();
            if (_brush != IntPtr.Zero)
                Native.DeleteObject(_brush);
            (_own, _system, _cancel, _focus, _brush) = (IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Native.SetThreadDpiAwarenessContext(previous);
        }

        return _choice;
    }

    private static IntPtr Create()
    {
        var instance = Native.GetModuleHandle(IntPtr.Zero);
        fixed (char* name = ClassName)
        {
            var windowClass = new Native.WndClassEx
            {
                Size = (uint)sizeof(Native.WndClassEx),
                WndProc = &WndProc,
                Instance = instance,
                Cursor = Native.LoadCursor(IntPtr.Zero, 32512),
                // HandPeg.exe's own icon when it has one (none today: Windows' default), as ProgressWindow.
                Icon = Native.LoadImage(instance, 32512, 1, 0, 0, 0x8040),
                ClassName = name,
            };
            Native.RegisterClassEx(windowClass);
        }

        // Its client area Width x Height DIPs at the main monitor's DPI, centred on that monitor's work area; Windows moves it to another
        // monitor's DPI with WM_DPICHANGED.
        var dpi = Native.GetDpiForSystem();
        var frame = new Native.Rect { Right = Scale(Width, dpi), Bottom = Scale(Height, dpi) };
        Native.AdjustWindowRectExForDpi(ref frame, Style, false, 0, dpi);
        var (width, height) = (frame.Right - frame.Left, frame.Bottom - frame.Top);
        Native.SystemParametersInfoW(0x0030, 0, out var work, 0); // SPI_GETWORKAREA
        var window = Native.CreateWindowEx(0, ClassName, "HandPeg", Style,
            work.Left + (work.Right - work.Left - width) / 2, work.Top + (work.Bottom - work.Top - height) / 2, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (window == IntPtr.Zero)
            return IntPtr.Zero;

        // The brush the buttons are erased with (else they flash light grey before they are drawn), the fonts, then the buttons.
        _brush = Native.CreateSolidBrush(Background);
        MakeFonts(Native.GetDpiForWindow(window));
        _own = Button(window, OwnCopyLabel, OwnCopyId, instance);
        _system = Button(window, SystemWideLabel, SystemWideId, instance);
        _cancel = Button(window, CancelLabel, CancelId, instance);
        Layout(window);

        // The dark title bar, in the window's own colour (Windows 11; earlier versions keep theirs).
        var on = 1;
        Native.DwmSetWindowAttribute(window, 20, ref on, sizeof(int));
        var caption = Background;
        Native.DwmSetWindowAttribute(window, 35, ref caption, sizeof(int));
        Native.ShowWindow(window, Native.SwShow);
        Native.SetForegroundWindow(window);
        Native.SetFocus(_own);
        return window;
    }

    private static IntPtr Button(IntPtr parent, string label, int id, IntPtr instance) =>
        Native.CreateWindowEx(0, "BUTTON", label, Native.WsChild | Native.WsVisible | Native.WsTabStop | Native.BsOwnerDraw, 0, 0, 0, 0, parent, id, instance, IntPtr.Zero);

    private static int Scale(int value, uint dpi) => (int)Math.Round(value * dpi / 96.0);

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case Native.WmEraseBkgnd:
                return 1;
            case Native.WmPaint:
                Paint(window);
                return IntPtr.Zero;
            case Native.WmSize:
                Layout(window);
                return IntPtr.Zero;
            case Native.WmCtlColorBtn:
                return _brush;
            case Native.WmDrawItem:
                DrawButton((Native.DrawItemStruct*)lParam);
                return 1;
            case Native.DmGetDefId:
                // Enter presses the focused button, else the recommended one.
                return (IntPtr)((Native.DcHasDefId << 16) | FocusedId());
            case Native.WmCommand:
                {
                    // A button's BN_CLICKED (notification 0), or IDOK / IDCANCEL from IsDialogMessage.
                    var id = (int)(wParam & 0xFFFF);
                    if ((((long)wParam >> 16) & 0xFFFF) != 0 || id is not (OwnCopyId or SystemWideId or CancelId))
                        return IntPtr.Zero;

                    _choice = id switch { OwnCopyId => DotNetChoice.OwnCopy, SystemWideId => DotNetChoice.SystemWide, _ => DotNetChoice.Cancel };
                    Native.DestroyWindow(window);
                    return IntPtr.Zero;
                }

            case Native.WmActivate:
                // Leaving the window: the focused button is remembered, and given the focus again when the window comes back.
                if ((wParam & 0xFFFF) == 0)
                    _focus = Native.GetFocus();
                break;
            case Native.WmSetFocus:
                Native.SetFocus(_focus != IntPtr.Zero && (_focus == _own || _focus == _system || _focus == _cancel) ? _focus : _own);
                return IntPtr.Zero;
            case Native.WmDpiChanged:
                {
                    // The fonts for the new monitor's DPI, the size Windows suggests for it, and the buttons laid out again.
                    MakeFonts((uint)(wParam & 0xFFFF));
                    var suggested = (Native.Rect*)lParam;
                    Native.SetWindowPos(window, IntPtr.Zero, suggested->Left, suggested->Top, suggested->Right - suggested->Left, suggested->Bottom - suggested->Top, NoZOrderNoActivate);
                    Layout(window);
                    Native.InvalidateRect(window, IntPtr.Zero, false);
                    foreach (var button in new[] { _own, _system, _cancel })
                        Native.InvalidateRect(button, IntPtr.Zero, false);
                    return IntPtr.Zero;
                }

            case Native.WmClose:
                _choice = DotNetChoice.Cancel;
                break;
            case Native.WmDestroy:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    /// <summary>The control id of the button with the keyboard focus; the recommended one's when none has it.</summary>
    private static int FocusedId()
    {
        var focus = Native.GetFocus();
        return focus == IntPtr.Zero ? OwnCopyId : focus == _system ? SystemWideId : focus == _cancel ? CancelId : OwnCopyId;
    }

    /// <summary>The buttons placed for the window's size and DPI: the two ways full width, Cancel at the right under them.</summary>
    private static void Layout(IntPtr window)
    {
        if (_own == IntPtr.Zero || _system == IntPtr.Zero || _cancel == IntPtr.Zero)
            return;

        var dpi = Native.GetDpiForWindow(window);
        Native.GetClientRect(window, out var client);
        var (left, right) = (Scale(Margin, dpi), client.Right - Scale(Margin, dpi));
        Native.SetWindowPos(_own, IntPtr.Zero, left, Scale(OwnTop, dpi), right - left, Scale(ButtonHeight, dpi), NoZOrderNoActivate);
        Native.SetWindowPos(_system, IntPtr.Zero, left, Scale(SystemTop, dpi), right - left, Scale(ButtonHeight, dpi), NoZOrderNoActivate);
        Native.SetWindowPos(_cancel, IntPtr.Zero, right - Scale(CancelWidth, dpi), Scale(CancelTop, dpi), Scale(CancelWidth, dpi), Scale(CancelHeight, dpi), NoZOrderNoActivate);
    }

    /// <summary>The heading, the words and each way's line of detail, drawn whole into a picture first (the buttons draw themselves).</summary>
    private static void Paint(IntPtr window)
    {
        var screen = Native.BeginPaint(window, out var paint);
        Native.GetClientRect(window, out var client);
        var (width, height) = (client.Right, client.Bottom);
        var dc = Native.CreateCompatibleDC(screen);
        var picture = Native.CreateCompatibleBitmap(screen, width, height);
        var old = Native.SelectObject(dc, picture);
        var dpi = Native.GetDpiForWindow(window);
        int S(int value) => Scale(value, dpi);
        const uint Words = Native.DtLeft | Native.DtWordBreak | Native.DtEditControl | Native.DtEndEllipsis | Native.DtNoPrefix;

        Fill(dc, client, Background);
        Native.SetBkMode(dc, Native.Transparent);
        var font = Native.SelectObject(dc, _headingFont);
        Native.SetTextColor(dc, Text);
        var at = new Native.Rect { Left = S(Margin), Top = S(20), Right = width - S(Margin), Bottom = S(48) };
        Native.DrawText(dc, _text.Heading, -1, ref at, Native.DtLeft | Native.DtSingleLine | Native.DtVCenter | Native.DtEndEllipsis | Native.DtNoPrefix);

        Native.SelectObject(dc, _bodyFont);
        Native.SetTextColor(dc, Muted);
        at = new Native.Rect { Left = S(Margin), Top = S(52), Right = width - S(Margin), Bottom = S(OwnTop - 10) };
        Native.DrawText(dc, _text.Body, -1, ref at, Words);

        Native.SelectObject(dc, _detailFont);
        at = new Native.Rect { Left = S(Margin), Top = S(OwnTop + ButtonHeight + 6), Right = width - S(Margin), Bottom = S(SystemTop - 10) };
        Native.DrawText(dc, _text.OwnDetail, -1, ref at, Words);
        at = new Native.Rect { Left = S(Margin), Top = S(SystemTop + ButtonHeight + 6), Right = width - S(Margin), Bottom = S(CancelTop - 10) };
        Native.DrawText(dc, _text.SystemDetail, -1, ref at, Words);

        Native.BitBlt(screen, 0, 0, width, height, dc, 0, 0, 0x00CC0020);
        Native.SelectObject(dc, font);
        Native.SelectObject(dc, old);
        Native.DeleteObject(picture);
        Native.DeleteDC(dc);
        Native.EndPaint(window, paint);
    }

    /// <summary>
    /// One button, drawn whole into a picture first: the recommended way filled in HandPeg's accent blue, the other in the track grey with
    /// an edge, Cancel quiet with an edge; darker while pressed, and a ring inside it while it has the keyboard focus (once keys are in use).
    /// </summary>
    private static void DrawButton(Native.DrawItemStruct* item)
    {
        var bounds = item->Bounds;
        var (width, height) = (bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        if (width <= 0 || height <= 0)
            return;

        var dpi = Native.GetDpiForWindow(item->Item);
        int S(int value) => Scale(value, dpi);
        var pressed = (item->ItemState & Native.OdsSelected) != 0;
        var focused = (item->ItemState & Native.OdsFocus) != 0 && (item->ItemState & Native.OdsNoFocusRect) == 0;
        var (face, edge, ink, ring, label) = (int)item->ControlId switch
        {
            OwnCopyId => (pressed ? BarPressed : Bar, pressed ? BarPressed : Bar, White, White, OwnCopyLabel),
            SystemWideId => (pressed ? TrackPressed : Track, Edge, Text, Text, SystemWideLabel),
            _ => (pressed ? Track : Background, Edge, pressed ? Text : Muted, Text, CancelLabel),
        };

        var dc = Native.CreateCompatibleDC(item->Dc);
        var picture = Native.CreateCompatibleBitmap(item->Dc, width, height);
        var old = Native.SelectObject(dc, picture);
        var local = new Native.Rect { Right = width, Bottom = height };
        Fill(dc, local, Background);
        Box(dc, local, face, edge, Math.Max(1, S(1)), S(8));
        if (focused)
            Box(dc, new Native.Rect { Left = S(3), Top = S(3), Right = width - S(3), Bottom = height - S(3) }, -1, ring, Math.Max(1, S(1)), S(6));

        Native.SetBkMode(dc, Native.Transparent);
        var font = Native.SelectObject(dc, _labelFont);
        Native.SetTextColor(dc, ink);
        var at = new Native.Rect { Left = S(12), Top = 0, Right = width - S(12), Bottom = height };
        Native.DrawText(dc, label, -1, ref at, Native.DtCenter | Native.DtVCenter | Native.DtSingleLine | Native.DtEndEllipsis | Native.DtNoPrefix);

        Native.BitBlt(item->Dc, bounds.Left, bounds.Top, width, height, dc, 0, 0, 0x00CC0020);
        Native.SelectObject(dc, font);
        Native.SelectObject(dc, old);
        Native.DeleteObject(picture);
        Native.DeleteDC(dc);
    }

    /// <summary>A rounded box outlined in <paramref name="edge"/> and filled with <paramref name="fill"/> (not filled when it is negative).</summary>
    private static void Box(IntPtr dc, Native.Rect rect, int fill, int edge, int thickness, int radius)
    {
        var brush = fill >= 0 ? Native.CreateSolidBrush(fill) : IntPtr.Zero;
        var pen = Native.CreatePen(Native.PsSolid, thickness, edge);
        var oldBrush = Native.SelectObject(dc, brush != IntPtr.Zero ? brush : Native.GetStockObject(Native.NullBrush));
        var oldPen = Native.SelectObject(dc, pen);
        Native.RoundRect(dc, rect.Left, rect.Top, rect.Right, rect.Bottom, radius, radius);
        Native.SelectObject(dc, oldPen);
        Native.SelectObject(dc, oldBrush);
        Native.DeleteObject(pen);
        if (brush != IntPtr.Zero)
            Native.DeleteObject(brush);
    }

    private static void Fill(IntPtr dc, Native.Rect rect, int color)
    {
        var brush = Native.CreateSolidBrush(color);
        Native.FillRect(dc, rect, brush);
        Native.DeleteObject(brush);
    }

    /// <summary>The dialog's fonts at <paramref name="dpi"/> (Segoe UI, ClearType): the heading, the words, the buttons' labels, the lines of detail.</summary>
    private static void MakeFonts(uint dpi)
    {
        DeleteFonts();
        _headingFont = Font(17, 600, dpi);
        _bodyFont = Font(13, 400, dpi);
        _labelFont = Font(13, 600, dpi);
        _detailFont = Font(12, 400, dpi);
    }

    private static IntPtr Font(int size, int weight, uint dpi) => Native.CreateFont(-Scale(size, dpi), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

    private static void DeleteFonts()
    {
        foreach (var font in new[] { _headingFont, _bodyFont, _labelFont, _detailFont })
        {
            if (font != IntPtr.Zero)
                Native.DeleteObject(font);
        }

        (_headingFont, _bodyFont, _labelFont, _detailFont) = (IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }
}
