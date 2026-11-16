using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

// Sequence Overview Scrubber thumbnails rendered from the composited sequence: GpuProgramStrip renders a sheet of the final picture
// on the background GPU after each edit settles; the strip, its hover previews and the Settings stage slice from it. The source file's
// keyframe sheet (FFmpeg hover sheet) is used only as a fallback when the GPU path is unavailable.
public partial class MainWindow
{
    private GpuProgramStrip? _programStrip;
    private ThumbnailSheet? _programSheet;

    // Sheet restored with a live-kept project tab (AdoptProgramSheet) and its undo mark: while the Sequence stays at that mark the sheet is
    // reused, not re-rendered. Null when the current sheet came from the renderer.
    private ProgramSheetKept? _programSheetKept;

    /// <summary>Undo mark (MainViewModel.UndoMark, null for none) a sheet was rendered or restored at.</summary>
    private sealed record ProgramSheetKept(object? UndoMark);

    // Undo mark of the current sheet: stamped on its request (GpuProgramStrip.SheetMark) or restored with a tab; null when unknown (no
    // sheet, or a partial one).
    private ProgramSheetKept? _programSheetDrawnAt;

    // Set when an edit arrives while the Overview Scrubber is hidden; the sheet renders once it becomes visible.
    private bool _programStale;
    private bool _programVisibilityHooked;

    /// <summary>Thumbnail sheet for the Overview Scrubber, its hover previews and the Settings stage (program sheet, else source sheet).</summary>
    private ThumbnailSheet? OverviewSheet => _programSheet ?? _sourceSheet;

    /// <summary>Requests a sheet for the current player state (PushGpuState); renders once the edit settles.</summary>
    private void RequestProgramStrip(SequenceExportState state)
    {
        if (!GpuProgramStrip.IsWanted || !(_viewModel.ShowTimelineThumbnails || _viewModel.ShowHoverPreviews))
            return;

        // Restored tab sheet: skip re-rendering its ~100 thumbnails while the Sequence is unchanged.
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

        // Defer rendering while the Overview Scrubber is hidden; IsVisibleChanged requests it on show.
        _programStale = !TimelineSlider.IsVisible;
        if (_programStale)
            return;

        ProgramStrip().Request(state, _viewModel.UndoMark);
    }

    /// <summary>Lazily created Overview Scrubber renderer (on the first state request, or a file pre-opened ahead of a drop).</summary>
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

            // First full sheet after a Game Preset drop: log drop-to-full-sequence latency.
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

    /// <summary>On opening another file or project (or none): drops the old sheet immediately; the new one follows its first state.</summary>
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
    /// Complete current sheet for a project tab moving to the background to keep; null when absent or partial. Returned only when rendered
    /// at the Sequence's current undo mark with nothing newer pending (<see cref="SheetIsCurrent"/>); otherwise it re-renders when the tab
    /// is reactivated.
    /// </summary>
    private ThumbnailSheet? SheetToKeep() =>
        _programSheet is { IsPartial: false } sheet && SheetIsCurrent(_programSheetDrawnAt is not null, _programSheetDrawnAt?.UndoMark, _viewModel.UndoMark,
            _programStale, _programStrip is { IsPending: true }) ? sheet : null;

    /// <summary>
    /// True when a complete sheet matches the current Sequence (pure; project-tabs-tabperf-review golden): its undo mark is known
    /// (<paramref name="stamped"/>) and identical to the current one, no hidden-scrubber edit is pending (<paramref name="stale"/>), and
    /// no request is settling or rendering (<paramref name="pending"/>).
    /// </summary>
    internal static bool SheetIsCurrent(bool stamped, object? drawnAt, object? undoMark, bool stale, bool pending) =>
        stamped && ReferenceEquals(drawnAt, undoMark) && !stale && !pending;

    /// <summary>
    /// Adopts the sheet of a live-kept project tab on reactivation: shown immediately and not re-rendered until the undo mark moves from
    /// <paramref name="undoMark"/>; the first edit renders as usual.
    /// </summary>
    private void AdoptProgramSheet(ThumbnailSheet sheet, object? undoMark)
    {
        _programStrip?.Clear();
        _programSheet = sheet;
        _programSheetDrawnAt = _programSheetKept = new ProgramSheetKept(undoMark);
        RebuildFilmstrip();
    }

    /// <summary>Thumbnail visibility setting changed: request a sheet if none exists yet.</summary>
    private void ProgramStripSettingChanged()
    {
        if (_programSheet is null && _gpu is not null && _viewModel.HasSource)
            RequestProgramStrip(_viewModel.LivePreviewState());
    }
}
