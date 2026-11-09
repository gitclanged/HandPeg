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

namespace HandPegApp.Views;

/// <summary>
/// The Voiceover Studio as a dock pane (5.4C; the Voiceover Studio window until then): where the voiceover is made, recorded from
/// a microphone with Record, Pause and Stop, or brought in from a file, then trimmed, placed and set to a level. The voiceover
/// belongs to the main window's settings, and the encode takes it as a second input. The recording lives in the view model, so it
/// goes on while the pane is behind another tab; closing the pane finishes it (<see cref="FinishRecording"/>). Every dock rebuild
/// takes the pane out and puts it back, so it listens to the view model only while it is loaded, and attaching twice does nothing.
/// Its keys are the main window's (in a side window too: the window hands them on).
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

    /// <summary>The bar follows the recording and the cuts for as long as the pane is in a window.</summary>
    private void Attach()
    {
        if (_attached)
            return;

        _attached = true;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.TimelineChanged += RedrawRecordBar;

        // The microphones are looked for the first time the studio is shown (not when HandPeg starts).
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

    /// <summary>The pane is closed in the middle of a recording: it is finished, so that nothing said is lost.</summary>
    public void FinishRecording()
    {
        if (_viewModel.StopVoiceoverCommand.CanExecute(null))
            _viewModel.StopVoiceoverCommand.Execute(null);
    }

    // ----- The recording timeline -----
    // A bar as long as the finished video, with a mark at every cut and a line that moves along it while
    // recording, so it can be seen how much of the video is still to be talked over.

    private readonly Stopwatch _recordClock = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Rectangle _recordedFill = new() { Opacity = 0.35 };
    private readonly Rectangle _cursor = new() { Width = 2, Fill = Brushes.OrangeRed };

    // A recording that was paused carries on from where its clock stopped; a new one starts from nothing.
    private bool _resuming;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsRecording) or nameof(MainViewModel.IsRecordingPaused))
            SyncRecordClock();
        else if (e.PropertyName is nameof(MainViewModel.DurationMs) or nameof(MainViewModel.HasVoiceover))
            RedrawRecordBar();
    }

    private void RecordBar_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawRecordBar();

    /// <summary>Starts, holds or resets the clock behind the moving line, following what the recorder is doing.</summary>
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

    /// <summary>Draws the bar: the marks where the cuts fall, and the line and the stretch recorded so far.</summary>
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

    /// <summary>Puts the line where the recording has got to, and says how much time is left.</summary>
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

    private async void DoOver_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.DiscardVoiceoverCommand.ExecuteAsync(null);
        _resuming = false;
        _recordClock.Reset();
        SyncRecordClock();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the voiceover",
            Filter = "Audio|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma|All files|*.*",
        };
        // Owned by the window the pane is in (the main window, or a side window).
        var owner = Window.GetWindow(this);
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true)
            await _viewModel.ImportVoiceoverAsync(dialog.FileName);
    }
}
