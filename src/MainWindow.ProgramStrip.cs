using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

// The Sequence Overview Scrubber's pictures drawn from the sequence itself (Pass 5.0-Verify, the user's call): GpuProgramStrip
// draws a sheet of the finished picture on the background card once each edit settles, and the strip, its hover previews and the
// Settings stage cut theirs from it. The opened file's key frames (FFmpeg's hover sheet) stand in only where the GPU cannot.
public partial class MainWindow
{
    private GpuProgramStrip? _programStrip;
    private ThumbnailSheet? _programSheet;

    // Pass 5.4D-TabPerf: the sheet a project tab kept in memory brought back with it (AdoptProgramSheet), and the undo mark it was drawn
    // at: until the Sequence changes from that, the sheet is the Sequence's and is not drawn again; null when the sheet up is the drawer's own.
    private ProgramSheetKept? _programSheetKept;

    /// <summary>A sheet a tab came back with, and the undo mark (MainViewModel.UndoMark, null for none) its Sequence was at.</summary>
    private sealed record ProgramSheetKept(object? UndoMark);

    // The undo mark the sheet up was drawn at (the 5.4D-TabPerf review): stamped on its request (GpuProgramStrip.SheetMark), or the one a
    // tab came back with; null while it is not known (none, or part of one).
    private ProgramSheetKept? _programSheetDrawnAt;

    // An edit came in while the Sequence Overview Scrubber was hidden: its pictures are drawn when it shows again.
    private bool _programStale;
    private bool _programVisibilityHooked;

    /// <summary>The sheet the Sequence Overview Scrubber, its hover previews and the Settings stage cut their pictures from.</summary>
    private ThumbnailSheet? OverviewSheet => _programSheet ?? _sourceSheet;

    /// <summary>The sequence as the player has it now (PushGpuState): its pictures drawn again once the edit settles.</summary>
    private void RequestProgramStrip(SequenceExportState state)
    {
        if (!GpuProgramStrip.IsWanted || !(_viewModel.ShowTimelineThumbnails || _viewModel.ShowHoverPreviews))
            return;

        // A tab brought back with its sheet (5.4D-TabPerf): its hundred pictures are not drawn again while its Sequence is as they show it.
        if (_programSheetKept is { } kept)
        {
            if (_programSheet is not null && ReferenceEquals(kept.UndoMark, _viewModel.UndoMark))
                return;
            _programSheetKept = null;
        }

        if (!_programVisibilityHooked)
        {
            _programVisibilityHooked = true;
            TimelineSlider.IsVisibleChanged += (_, _) =>
            {
                if (TimelineSlider.IsVisible && _programStale && _viewModel.HasSource)
                    RequestProgramStrip(_viewModel.LivePreviewState());
            };
        }

        // Nothing shows the pictures while the Sequence Overview Scrubber is hidden: drawn when it is shown.
        _programStale = !TimelineSlider.IsVisible;
        if (_programStale)
            return;

        ProgramStrip().Request(state, _viewModel.UndoMark);
    }

    /// <summary>The Sequence Overview Scrubber's drawer, made the first time it is wanted (a state to draw, or a file to open ahead of a drop).</summary>
    private GpuProgramStrip ProgramStrip()
    {
        if (_programStrip is not null)
            return _programStrip;

        _programStrip = new GpuProgramStrip();
        _programStrip.SheetReady += sheet =>
        {
            if (!_viewModel.HasSource)
                return;
            _programSheet = sheet;
            _programSheetKept = null;
            _programSheetDrawnAt = sheet.IsPartial ? null : new ProgramSheetKept(_programStrip?.SheetMark);
            RebuildFilmstrip();

            // The first full sheet after a drop on a Game Preset: how long the drop took to show the whole sequence (the log).
            if (!sheet.IsPartial && Interlocked.Exchange(ref _dropClock, null) is { } clock)
                AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Drop on a Game Preset: the Sequence Overview Scrubber's full sheet in {clock.ElapsedMilliseconds} ms."));
        };
        _programStrip.GaveUp += () =>
        {
            _programSheet = null;
            _programSheetDrawnAt = null;
            _viewModel.UseSourceThumbnails();
            RebuildFilmstrip();
        };
        return _programStrip;
    }

    /// <summary>Another file or project opened, or none: the last one's pictures go at once (the new ones follow its first state).</summary>
    private void ClearProgramStrip()
    {
        _programStrip?.Clear();
        (_programSheetKept, _programSheetDrawnAt) = (null, null);
        if (_programSheet is null)
            return;
        _programSheet = null;
        RebuildFilmstrip();
    }

    /// <summary>
    /// The full sheet the Sequence Overview Scrubber shows now, for a project tab going behind to keep (Pass 5.4D-TabPerf); null while
    /// there is none, or only part of one (the drawer had not finished). The 5.4D-TabPerf review: only when it was drawn at the undo mark
    /// the Sequence is at now and no newer one is coming (<see cref="SheetIsCurrent"/>), else none: it is drawn again as the tab comes back.
    /// </summary>
    private ThumbnailSheet? SheetToKeep() =>
        _programSheet is { IsPartial: false } sheet && SheetIsCurrent(_programSheetDrawnAt is not null, _programSheetDrawnAt?.UndoMark, _viewModel.UndoMark,
            _programStale, _programStrip is { IsPending: true }) ? sheet : null;

    /// <summary>
    /// Whether a whole sheet is the Sequence's as it is now (pure, 5.4D-TabPerf review: the goldens' project-tabs-tabperf-review): its undo
    /// mark known (<paramref name="stamped"/>) and the very one the Sequence is at, no edit waiting while the scrubber was hidden
    /// (<paramref name="stale"/>), and no request settling or sheet being drawn (<paramref name="pending"/>).
    /// </summary>
    internal static bool SheetIsCurrent(bool stamped, object? drawnAt, object? undoMark, bool stale, bool pending) =>
        stamped && ReferenceEquals(drawnAt, undoMark) && !stale && !pending;

    /// <summary>
    /// A project tab kept in memory came back to the front with its sheet (Pass 5.4D-TabPerf): shown at once, and not drawn again until its
    /// Sequence changes (its undo mark moves from <paramref name="undoMark"/>); the first edit draws it as any edit does.
    /// </summary>
    private void AdoptProgramSheet(ThumbnailSheet sheet, object? undoMark)
    {
        _programStrip?.Clear();
        _programSheet = sheet;
        _programSheetDrawnAt = _programSheetKept = new ProgramSheetKept(undoMark);
        RebuildFilmstrip();
    }

    /// <summary>The setting that shows the pictures changed: drawn now when they were not.</summary>
    private void ProgramStripSettingChanged()
    {
        if (_programSheet is null && _gpu is not null && _viewModel.HasSource)
            RequestProgramStrip(_viewModel.LivePreviewState());
    }
}
