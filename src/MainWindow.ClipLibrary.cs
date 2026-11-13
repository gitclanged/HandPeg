using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Docking;
using HandPegApp.Models;
using HandPegApp.Views;

namespace HandPegApp;

// ----- The Personal Clip Library on a clip's menu (Pass 5.2C, Services/ClipLibrary.cs) -----
public partial class MainWindow
{
    /// <summary>How long a clip dragged from the Timeline is held over the Clip Library's button before the pane shows the Clip Library (5.4.4).</summary>
    internal const double LibraryDwellMs = 180;

    private const string LibraryDropWords = "+ Save Clip to Personal Clip Library...";

    // A clip dragged from the Timeline onto the Clip Library (5.4.4), all made at its first use: the guide over where it would go, the
    // dwell over the Clip Library's button, the zone the pointer is in (since when, where the guide was put, the pointer on the
    // screen), and whether the dragged block can be saved at all (asked once per drag).
    private DockGuide? _libraryGuide;
    private DispatcherTimer? _libraryDwell;
    private LibraryDropZone _libraryZone;
    private long _libraryZoneSince;
    private Rect _libraryGuideAt = Rect.Empty;
    private Point _libraryPointer;
    private (TimelineBlock Block, bool Saveable)? _libraryDrag;

    /// <summary>The media browser lists the Clip Library again when a clip goes into it; a clip dragged from the Timeline onto it saves it (5.4.4).</summary>
    private void InitializeClipLibrary()
    {
        _viewModel.ClipLibraryChanged += () => MediaBrowser.LibraryChanged();
        var surface = UnifiedTimeline.Surface;
        surface.DragOutside = Timeline_DragOutside;
        surface.DropOutside = Timeline_DropOutside;
        surface.DragEnded += ForgetLibraryDrag;
    }

    /// <summary>"Save Clip to Personal Clip Library...": a title and tags asked for, then the span kept in the background.</summary>
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
    /// What a clip dragged out of the Timeline does over the Media pane (5.4.4), for a zone and how long the pointer has been in it:
    /// whether a drop is offered there, and whether the pane shows the Clip Library now (held <see cref="LibraryDwellMs"/> on its button).
    /// </summary>
    internal static (bool Offers, bool ShowLibrary) LibraryDropDecision(LibraryDropZone zone, double heldMs) => zone switch
    {
        LibraryDropZone.Library => (true, false),
        LibraryDropZone.Tab => (true, heldMs >= LibraryDwellMs),
        _ => (false, false),
    };

    // A move of a clip's drag outside the Timeline (screen pixels; null: back inside). True offers the drop: the guide is shown over the
    // Clip Library (or its button, the dwell started there) and the block waits where it began.
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

    // Let go where a drop was offered: the clip's "Save Clip to Personal Clip Library..." asked for once the mouse is let go and the rows
    // are laid out again (never inside the release). The Timeline itself is not changed.
    private bool Timeline_DropOutside(TimelineRow row, TimelineBlock block, Point screen)
    {
        if (block.Clip is not Layer clip || !SaveableByDrag(block) || !LibraryDropDecision(MediaBrowser.LibraryDropZoneAt(screen), 0).Offers)
            return false;

        HideLibraryDrop();
        Dispatcher.InvokeAsync(() => SaveToClipLibrary(clip), DispatcherPriority.Input);
        return true;
    }

    // Whether the dragged block is a clip the Clip Library can keep (a video, a sound or a picture of a file), asked once per drag; a
    // group's drag saves the pressed clip only.
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

    // Held on the Clip Library's button: the pane shows the Clip Library, and the guide then covers the whole pane.
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
            // (A tick a little early: the rest of the dwell.)
            _libraryDwell!.Interval = TimeSpan.FromMilliseconds(Math.Max(1, LibraryDwellMs - held));
            _libraryDwell.Start();
            return;
        }

        MediaBrowser.ShowClipLibrary();
        if (MediaBrowser.ShowsClipLibrary)
            (_libraryZone, _libraryZoneSince) = (LibraryDropZone.Library, Environment.TickCount64);
        ShowLibraryGuide();
    }

    // The guide over where the clip would go (the pane, with its words; or the Clip Library's button, without), moved only when that changed.
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
            // Owned by the window, so it goes with it; a switch to another program never leaves it on top of that.
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

    // Nothing offered any more (the pointer left the pane, came back inside the Timeline, or the program lost the foreground).
    private void HideLibraryDrop()
    {
        _libraryDwell?.Stop();
        _libraryZone = LibraryDropZone.None;
        HideLibraryGuide();
    }

    // A drag ended (dropped, committed, refused or taken away): nothing of it is kept.
    private void ForgetLibraryDrag()
    {
        HideLibraryDrop();
        _libraryDrag = null;
    }
}
