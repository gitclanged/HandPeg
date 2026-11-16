using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HandPegApp.Native;
using static HandPegApp.Native.User32;

namespace HandPegApp.Views;

/// <summary>Media pane drop zone under a Timeline clip drag: none, the Clip Library button, or the Clip Library itself.</summary>
public enum LibraryDropZone
{
    None,
    Tab,
    Library,
}

// ----- Timeline clip drag onto the Clip Library -----
// On each move of a clip dragged out of the Timeline the window queries the zone under the pointer: the Clip Library (anywhere on the
// pane) while shown, otherwise its title-row button (a dwell there makes the window show it). Returns none while the pane is a
// background tab, windowless, covered by another window, or in New Project picker mode.
public partial class MediaBrowserPane
{
    private const uint RootAncestor = 2; // GA_ROOT


    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr window, uint flags);

    /// <summary>True while the pane shows the Clip Library.</summary>
    public bool ShowsClipLibrary => _libraryMode;

    /// <summary>Switches to the Clip Library (after a button dwell); no-op in picker mode.</summary>
    public void ShowClipLibrary()
    {
        if (!PickerMode && LibrarySide.IsChecked != true)
            LibrarySide.IsChecked = true;
    }

    /// <summary>Drop zone at a screen point in physical pixels (see <see cref="LibraryDropZone"/>).</summary>
    public LibraryDropZone LibraryDropZoneAt(Point screen)
    {
        if (PickerMode || !IsVisible || PresentationSource.FromVisual(this) is not HwndSource source || !IsTopWindowAt(screen, source))
            return LibraryDropZone.None;

        var onButton = Inside(LibrarySide, screen);
        if (_libraryMode)
            return onButton || Inside(this, screen) ? LibraryDropZone.Library : LibraryDropZone.None;
        return onButton ? LibraryDropZone.Tab : LibraryDropZone.None;
    }

    /// <summary>Screen rect in physical pixels of the Clip Library button (<paramref name="tab"/>) or the whole pane; empty when windowless.</summary>
    public Rect LibraryDropRect(bool tab)
    {
        FrameworkElement element = tab ? LibrarySide : this;
        if (PresentationSource.FromVisual(element) is null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return Rect.Empty;
        return new Rect(element.PointToScreen(new Point(0, 0)), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
    }

    // True when a screen point lies on a visible element (mapped via PointFromScreen into its window's coordinates).
    private static bool Inside(FrameworkElement element, Point screen)
    {
        if (!element.IsVisible || PresentationSource.FromVisual(element) is null)
            return false;
        var at = element.PointFromScreen(screen);
        return at.X >= 0 && at.Y >= 0 && at.X < element.ActualWidth && at.Y < element.ActualHeight;
    }

    // True when the root window at the point (WindowFromPoint skips click-through windows such as the drop guide) is the pane's own;
    // any other window on top (side window, dialog) blocks the drop.
    private static bool IsTopWindowAt(Point screen, HwndSource source)
    {
        var under = WindowFromPoint(new NativePoint { X = (int)Math.Floor(screen.X), Y = (int)Math.Floor(screen.Y) });
        return under != IntPtr.Zero && source.Handle != IntPtr.Zero && GetAncestor(under, RootAncestor) == GetAncestor(source.Handle, RootAncestor);
    }
}
