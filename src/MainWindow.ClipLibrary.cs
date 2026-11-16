using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Docking;
using HandPegApp.Models;
using HandPegApp.Views;

namespace HandPegApp;

// ----- Personal Clip Library: clip menu save and Timeline drag-to-library (Services/ClipLibrary.cs) -----
public partial class MainWindow
{
    /// <summary>Hover dwell over the Clip Library button during a Timeline clip drag before the pane switches to it, ms.</summary>
    internal const double LibraryDwellMs = 180;

    private const string LibraryDropWords = "+ Save Clip to Personal Clip Library...";

    // Drag-to-library state (created lazily): drop guide, button dwell timer, current zone with entry time, guide rect and screen pointer,
    // and the per-drag saveability cache.
    private DockGuide? _libraryGuide;
    private DispatcherTimer? _libraryDwell;
    private LibraryDropZone _libraryZone;
    private long _libraryZoneSince;
    private Rect _libraryGuideAt = Rect.Empty;
    private Point _libraryPointer;
    private (TimelineBlock Block, bool Saveable)? _libraryDrag;

    /// <summary>Wires library refresh on save and the Timeline drag-out handlers for drag-to-library saving.</summary>
    private void InitializeClipLibrary()
    {
        _viewModel.ClipLibraryChanged += () => MediaBrowser.LibraryChanged();
        var surface = UnifiedTimeline.Surface;
        surface.DragOutside = Timeline_DragOutside;
        surface.DropOutside = Timeline_DropOutside;
        surface.DragEnded += ForgetLibraryDrag;
    }

    /// <summary>"Save Clip to Personal Clip Library...": prompts for title and tags, then saves the span in the background.</summary>
    private void SaveToClipLibrary(Layer clip)
    {
        if (_viewModel.ClipOf(clip) is not { } model)
            return;

        var what = string.Create(CultureInfo.InvariantCulture, $"{model.Duration:0.#} seconds of {System.IO.Path.GetFileName(model.SourcePath)}, kept as it is (nothing encoded again) in Documents\\HandPeg\\Clips");
        if (_viewModel.FocalIn(clip) is { } focal)
            what += string.Create(CultureInfo.InvariantCulture, $", with the focus of its highlight ({focal.X:0%} across, {focal.Y:0%} down).");
        else
            what += ".";

        var dialog = new SaveToClipLibraryDialog(clip.Name, what) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        _ = _viewModel.SaveToClipLibraryAsync(clip, dialog.ClipTitle, dialog.Tags);
    }

    /// <summary>
    /// Drop decision for a Timeline clip dragged over the Media pane, given the zone and dwell time: whether a drop is offered, and whether
    /// to switch the pane to the Clip Library (after <see cref="LibraryDwellMs"/> on its button).
    /// </summary>
    internal static (bool Offers, bool ShowLibrary) LibraryDropDecision(LibraryDropZone zone, double heldMs) => zone switch
    {
        LibraryDropZone.Library => (true, false),
        LibraryDropZone.Tab => (true, heldMs >= LibraryDwellMs),
        _ => (false, false),
    };

    // Timeline drag moved outside (screen pixels; null = back inside). Returning true offers the drop: the guide shows over the Clip
    // Library (or its button, starting the dwell) and the block stays parked at its origin.
    private bool Timeline_DragOutside(TimelineRow row, TimelineBlock block, Point? screen)
    {
        if (screen is not { } at || !SaveableByDrag(block))
        {
            HideLibraryDrop();
            return false;
        }

        _libraryPointer = at;
        var zone = MediaBrowser.LibraryDropZoneAt(at);
        if (!LibraryDropDecision(zone, 0).Offers)
        {
            HideLibraryDrop();
            return false;
        }

        if (zone != _libraryZone)
        {
            (_libraryZone, _libraryZoneSince) = (zone, Environment.TickCount64);
            if (zone == LibraryDropZone.Tab)
                StartLibraryDwell();
            else
                _libraryDwell?.Stop();
        }

        ShowLibraryGuide();
        return true;
    }

    // Release over an offered drop: queue the save dialog after the release completes and rows re-layout (never inside the mouse-up
    // handler). The Timeline is not modified.
    private bool Timeline_DropOutside(TimelineRow row, TimelineBlock block, Point screen)
    {
        if (block.Clip is not Layer clip || !SaveableByDrag(block) || !LibraryDropDecision(MediaBrowser.LibraryDropZoneAt(screen), 0).Offers)
            return false;

        HideLibraryDrop();
        Dispatcher.InvokeAsync(() => SaveToClipLibrary(clip), DispatcherPriority.Input);
        return true;
    }

    // True when the dragged block is a savable file-backed clip (video, audio or image); cached per drag. Group drags save only the
    // pressed clip.
    private bool SaveableByDrag(TimelineBlock block)
    {
        if (_libraryDrag is { } known && ReferenceEquals(known.Block, block))
            return known.Saveable;

        var saveable = block.Clip is Layer clip && _viewModel.CanSaveToClipLibrary(clip);
        _libraryDrag = (block, saveable);
        return saveable;
    }

    private void StartLibraryDwell()
    {
        if (_libraryDwell is null)
        {
            _libraryDwell = new DispatcherTimer(DispatcherPriority.Input);
            _libraryDwell.Tick += LibraryDwell_Tick;
        }

        _libraryDwell.Stop();
        _libraryDwell.Interval = TimeSpan.FromMilliseconds(LibraryDwellMs);
        _libraryDwell.Start();
    }

    // Dwell elapsed on the Clip Library button: switch the pane to the library and expand the guide to the whole pane.
    private void LibraryDwell_Tick(object? sender, EventArgs e)
    {
        _libraryDwell?.Stop();
        if (_libraryZone != LibraryDropZone.Tab || !UnifiedTimeline.Surface.IsDragParked)
            return;

        var zone = MediaBrowser.LibraryDropZoneAt(_libraryPointer);
        var held = Environment.TickCount64 - _libraryZoneSince;
        if (zone != LibraryDropZone.Tab)
            return;
        if (!LibraryDropDecision(zone, held).ShowLibrary)
        {
            // Timer fired early: wait out the remaining dwell.
            _libraryDwell!.Interval = TimeSpan.FromMilliseconds(Math.Max(1, LibraryDwellMs - held));
            _libraryDwell.Start();
            return;
        }

        MediaBrowser.ShowClipLibrary();
        if (MediaBrowser.ShowsClipLibrary)
            (_libraryZone, _libraryZoneSince) = (LibraryDropZone.Library, Environment.TickCount64);
        ShowLibraryGuide();
    }

    // Shows the drop guide over the target (the pane with caption text, or the button without); repositions only on change.
    private void ShowLibraryGuide()
    {
        var onButton = _libraryZone == LibraryDropZone.Tab;
        var rect = MediaBrowser.LibraryDropRect(onButton);
        if (rect.IsEmpty)
        {
            HideLibraryGuide();
            return;
        }

        if (onButton)
            rect.Inflate(3, 3);
        if (rect == _libraryGuideAt && _libraryGuide is { IsVisible: true })
            return;

        if (_libraryGuide is null)
        {
            // Window-owned so it follows the main window; app deactivation hides it so it never stays above other programs.
            _libraryGuide = new DockGuide((Brush?)TryFindResource("AccentBrush") ?? Brushes.SteelBlue) { Owner = this };
            Application.Current.Deactivated += (_, _) => HideLibraryDrop();
        }

        _libraryGuideAt = rect;
        _libraryGuide.ShowAt(rect, onButton ? "" : LibraryDropWords);
    }

    private void HideLibraryGuide()
    {
        _libraryGuideAt = Rect.Empty;
        if (_libraryGuide is { IsVisible: true } guide)
            guide.Hide();
    }

    // Withdraws the offer (pointer left the pane, re-entered the Timeline, or the app lost focus).
    private void HideLibraryDrop()
    {
        _libraryDwell?.Stop();
        _libraryZone = LibraryDropZone.None;
        HideLibraryGuide();
    }

    // Drag ended (dropped, committed, refused or cancelled): clear all drag-to-library state.
    private void ForgetLibraryDrag()
    {
        HideLibraryDrop();
        _libraryDrag = null;
    }
}
