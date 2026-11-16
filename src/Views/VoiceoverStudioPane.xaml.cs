using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HandPegApp.ViewModels;
using Microsoft.Win32;

using HandPegApp.Services;

namespace HandPegApp.Views;

/// <summary>
/// Voiceover Studio dock pane: record from a microphone (Record, Pause, Stop) or import a file, then trim, place and set level. The
/// voiceover is part of the main window's settings and the encode takes it as a second input. Recording state lives in the view model,
/// so it continues while the pane is a background tab; closing the pane finishes it (<see cref="FinishRecording"/>). Dock rebuilds
/// detach and re-attach the pane, so it subscribes only while loaded and Attach is idempotent. Shortcuts route through the main
/// window (side windows forward them).
/// </summary>
public partial class VoiceoverStudioPane : UserControl
{
    private readonly MainViewModel _viewModel;
    private bool _attached, _microphonesAsked;

    public VoiceoverStudioPane(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _timer.Tick += (_, _) => UpdateCursor();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Subscribes while loaded so the record bar tracks recording state and cuts.</summary>
    private void Attach()
    {
        if (_attached)
            return;

        _attached = true;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.TimelineChanged += RedrawRecordBar;

        // Enumerate microphones lazily on first show (not at start-up).
        if (!_microphonesAsked && _viewModel.Microphones.Count == 0)
            _viewModel.RefreshMicrophonesCommand.Execute(null);
        _microphonesAsked = true;
        SyncRecordClock();
        RedrawRecordBar();
    }

    private void Detach()
    {
        if (!_attached)
            return;

        _attached = false;
        _timer.Stop();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.TimelineChanged -= RedrawRecordBar;
    }

    /// <summary>Finishes an active recording when the pane closes, so nothing recorded is lost.</summary>
    public void FinishRecording()
    {
        if (_viewModel.StopVoiceoverCommand.CanExecute(null))
            _viewModel.StopVoiceoverCommand.Execute(null);
    }

    // ----- Recording timeline -----
    // A bar spanning the output duration, with a tick at every cut and a cursor advancing during recording,
    // showing how much video remains to be narrated.

    private readonly Stopwatch _recordClock = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Rectangle _recordedFill = new() { Opacity = 0.35 };
    private readonly Rectangle _cursor = new() { Width = 2, Fill = Brushes.OrangeRed };

    // True after a pause: resuming continues the clock; a new recording resets it.
    private bool _resuming;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsRecording) or nameof(MainViewModel.IsRecordingPaused))
            SyncRecordClock();
        else if (e.PropertyName is nameof(MainViewModel.DurationMs) or nameof(MainViewModel.HasVoiceover))
            RedrawRecordBar();
    }

    private void RecordBar_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawRecordBar();

    /// <summary>Starts, pauses or resets the cursor clock to match the recorder state.</summary>
    private void SyncRecordClock()
    {
        if (_viewModel.IsRecording)
        {
            if (!_recordClock.IsRunning)
            {
                if (!_resuming)
                    _recordClock.Reset();
                _recordClock.Start();
            }

            if (_attached)
                _timer.Start();
        }
        else
        {
            _recordClock.Stop();
            _timer.Stop();
        }

        _resuming = _viewModel.IsRecordingPaused;
        DoOverButton.IsEnabled = _viewModel.IsRecording || _viewModel.IsRecordingPaused || _viewModel.HasVoiceover;
        UpdateCursor();
    }

    /// <summary>Rebuilds the bar: cut ticks, the recorded-span fill and the cursor.</summary>
    private void RedrawRecordBar()
    {
        var total = _viewModel.OutputDurationSeconds;
        var cuts = _viewModel.GetCutSplitFractions();
        DurationText.Text = string.Create(CultureInfo.InvariantCulture, $"Timeline Duration: {total:0.#}s")
                            + (total <= 0 ? "  (load a video first)"
                                : cuts.Count == 0 ? "  (one video clip)"
                                : $"  ({cuts.Count + 1} video clips)");

        RecordBar.Children.Clear();
        var (width, height) = (RecordBar.ActualWidth, RecordBar.ActualHeight);
        if (width <= 0 || height <= 0)
            return;

        _recordedFill.Height = _cursor.Height = height;
        _recordedFill.Fill = (Brush)FindResource("AccentBrush");
        RecordBar.Children.Add(_recordedFill);

        foreach (var cut in cuts)
        {
            var tick = new Rectangle { Width = 1, Height = height * 0.6, Fill = (Brush)FindResource("MutedTextBrush") };
            Canvas.SetLeft(tick, cut * width);
            Canvas.SetTop(tick, height * 0.2);
            RecordBar.Children.Add(tick);
        }

        RecordBar.Children.Add(_cursor);
        DoOverButton.IsEnabled = _viewModel.IsRecording || _viewModel.IsRecordingPaused || _viewModel.HasVoiceover;
        UpdateCursor();
    }

    /// <summary>Positions the cursor at the recorded time and updates the remaining-time readout.</summary>
    private void UpdateCursor()
    {
        var total = _viewModel.OutputDurationSeconds;
        var elapsed = _recordClock.Elapsed.TotalSeconds;
        var reached = total > 0 ? Math.Clamp(elapsed / total, 0, 1) * RecordBar.ActualWidth : 0;
        Canvas.SetLeft(_cursor, Math.Max(reached - 1, 0));
        _recordedFill.Width = reached;

        var active = _viewModel.IsRecording || _viewModel.IsRecordingPaused;
        RecordTimeText.SetResourceReference(TextBlock.ForegroundProperty, active && total > 0 && elapsed > total ? "WarningTextBrush" : "MutedTextBrush");
        RecordTimeText.Text = total <= 0 ? ""
            : elapsed <= 0 ? "Press Record: the line moves along the bar as you speak."
            : elapsed > total ? string.Create(CultureInfo.InvariantCulture, $"{elapsed:0.0} s recorded: {elapsed - total:0.0} s past the end of the video")
            : string.Create(CultureInfo.InvariantCulture, $"{elapsed:0.0} s recorded, {total - elapsed:0.0} s left");
    }

    private void DoOver_Click(object sender, RoutedEventArgs e) => Faults.Forget(DoOver_ClickAsync(sender, e), nameof(DoOver_Click));

    private async Task DoOver_ClickAsync(object sender, RoutedEventArgs e)
    {
        await _viewModel.DiscardVoiceoverCommand.ExecuteAsync(null);
        _resuming = false;
        _recordClock.Reset();
        SyncRecordClock();
    }

    private void Import_Click(object sender, RoutedEventArgs e) => Faults.Forget(Import_ClickAsync(sender, e), nameof(Import_Click));

    private async Task Import_ClickAsync(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the voiceover",
            Filter = "Audio|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma|All files|*.*",
        };
        // Owner is the hosting window (main or side window).
        var owner = Window.GetWindow(this);
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true)
            await _viewModel.ImportVoiceoverAsync(dialog.FileName);
    }
}
