using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HandPegApp.Views;

/// <summary>What of the Media pane is under a clip dragged from the Timeline (5.4.4): nothing that takes it, the Clip Library's button, or the Clip Library.</summary>
public enum LibraryDropZone
{
    None,
    Tab,
    Library,
}

// ----- A clip dragged from the Timeline onto the Clip Library (5.4.4) -----
// The window asks, at each move of a clip dragged out of the Timeline, what of this pane is under the pointer: the Clip Library while
// it is shown (anywhere on the pane), its button in the title row while it is not (held there a moment, the window shows it). Nothing
// while the pane is hidden behind another tab, in no window, covered by another window, or the New Project studio's picker.
public partial class MediaBrowserPane
{
    private const uint RootAncestor = 2; // GA_ROOT

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(ScreenPoint point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr window, uint flags);

    /// <summary>Whether the pane shows the Clip Library now.</summary>
    public bool ShowsClipLibrary => _libraryMode;

    /// <summary>Shows the Clip Library (a clip held over its button); nothing for the New Project studio's picker.</summary>
    public void ShowClipLibrary()
    {
        if (!PickerMode && LibrarySide.IsChecked != true)
            LibrarySide.IsChecked = true;
    }

    /// <summary>What of the pane is under a point of the screen (its real pixels): see <see cref="LibraryDropZone"/>.</summary>
    public LibraryDropZone LibraryDropZoneAt(Point screen)
    {
        if (PickerMode || !IsVisible || PresentationSource.FromVisual(this) is not HwndSource source || !IsTopWindowAt(screen, source))
            return LibraryDropZone.None;

        var onButton = Inside(LibrarySide, screen);
        if (_libraryMode)
            return onButton || Inside(this, screen) ? LibraryDropZone.Library : LibraryDropZone.None;
        return onButton ? LibraryDropZone.Tab : LibraryDropZone.None;
    }

    /// <summary>Where on the screen (real pixels) the Clip Library's button (<paramref name="tab"/>) or the pane is; empty while it is in no window.</summary>
    public Rect LibraryDropRect(bool tab)
    {
        FrameworkElement element = tab ? LibrarySide : this;
        if (PresentationSource.FromVisual(element) is null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return Rect.Empty;
        return new Rect(element.PointToScreen(new Point(0, 0)), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
    }

    // Whether a point of the screen is on an element that is shown (its own window's coordinates, through PointFromScreen).
    private static bool Inside(FrameworkElement element, Point screen)
    {
        if (!element.IsVisible || PresentationSource.FromVisual(element) is null)
            return false;
        var at = element.PointFromScreen(screen);
        return at.X >= 0 && at.Y >= 0 && at.X < element.ActualWidth && at.Y < element.ActualHeight;
    }

    // Whether the window Windows finds at the point (see-through, click-through windows such as the drop guide passed by) is the pane's
    // own: another window over it (a side window, a dialog) takes nothing.
    private static bool IsTopWindowAt(Point screen, HwndSource source)
    {
        var under = WindowFromPoint(new ScreenPoint { X = (int)Math.Floor(screen.X), Y = (int)Math.Floor(screen.Y) });
        return under != IntPtr.Zero && source.Handle != IntPtr.Zero && GetAncestor(under, RootAncestor) == GetAncestor(source.Handle, RootAncestor);
    }
}
