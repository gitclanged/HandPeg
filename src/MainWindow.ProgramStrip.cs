using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

// The Overview Bar's pictures drawn from the sequence itself (Pass 5.0-Verify, the user's call): GpuProgramStrip draws a
// sheet of the finished picture on the background card once each edit settles, and the strip, its hover previews and the
// Settings stage cut theirs from it. The main file's key frames (FFmpeg's hover sheet) stand in only where the GPU cannot.
public partial class MainWindow
{
    private GpuProgramStrip? _programStrip;
    private ThumbnailSheet? _programSheet;

    // An edit came in while the Overview Bar was hidden: its pictures are drawn when it shows again.
    private bool _programStale;
    private bool _programVisibilityHooked;

    /// <summary>The sheet the Overview Bar, its hover previews and the Settings stage cut their pictures from.</summary>
    private ThumbnailSheet? OverviewSheet => _programSheet ?? _sourceSheet;

    /// <summary>The sequence as the player has it now (PushGpuState): its pictures drawn again once the edit settles.</summary>
    private void RequestProgramStrip(SequenceExportState state)
    {
        if (!GpuProgramStrip.IsWanted || !(_viewModel.ShowTimelineThumbnails || _viewModel.ShowHoverPreviews))
            return;

        if (!_programVisibilityHooked)
        {
            _programVisibilityHooked = true;
            TimelineSlider.IsVisibleChanged += (_, _) =>
            {
                if (TimelineSlider.IsVisible && _programStale && _viewModel.HasSource)
                    RequestProgramStrip(_viewModel.LivePreviewState());
            };
        }

        // Nothing shows the pictures while the Overview Bar is hidden: drawn when it is shown.
        _programStale = !TimelineSlider.IsVisible;
        if (_programStale)
            return;

        ProgramStrip().Request(state);
    }

    /// <summary>The Overview Bar's drawer, made the first time it is wanted (a state to draw, or a file to open ahead of a drop).</summary>
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
            RebuildFilmstrip();

            // The first full sheet after a drop on a style: how long the drop took to show the whole sequence (the log).
            if (Interlocked.Exchange(ref _dropClock, null) is { } clock)
                AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Drop on a style: the Overview Bar's full sheet in {clock.ElapsedMilliseconds} ms."));
        };
        _programStrip.GaveUp += () =>
        {
            _programSheet = null;
            _viewModel.UseSourceThumbnails();
            RebuildFilmstrip();
        };
        return _programStrip;
    }

    /// <summary>Another file or project opened, or none: the last one's pictures go at once (the new ones follow its first state).</summary>
    private void ClearProgramStrip()
    {
        _programStrip?.Clear();
        if (_programSheet is null)
            return;
        _programSheet = null;
        RebuildFilmstrip();
    }

    /// <summary>The setting that shows the pictures changed: drawn now when they were not.</summary>
    private void ProgramStripSettingChanged()
    {
        if (_programSheet is null && _gpu is not null && _viewModel.HasSource)
            RequestProgramStrip(_viewModel.LivePreviewState());
    }
}
