using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp.Views;

/// <summary>
/// The Subtitle Editor: the subtitle track's cues on a timeline of their own, with a small player that shows
/// nothing but the subtitles. The player is the editor's own, a second libmpv playing a blank black picture
/// the shape of the output frame, so retiming a cue is seen at once and costs none of what the main window's
/// player, with its graph of layers, would. The main window is told where the editor's playhead is only once
/// it has rested for half a second, and draws no subtitles of its own while the editor is open.
/// </summary>
public partial class SubtitleEditorWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly bool _images;

    // The editor's player; null until it has started, and when libmpv is not there.
    private MpvPlayer? _player;
    private bool _closed;

    // Tells the main window where the playhead is: restarted by every move, so it fires half a second after the last.
    private readonly DispatcherTimer _mainSync = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // Writes the subtitles for the editor's player again: at most every so often while a cue is dragged or typed in.
    private readonly DispatcherTimer _previewRefresh = new() { Interval = TimeSpan.FromMilliseconds(60) };

    // Sends the player where the playhead is, no more often than it can keep up with: a scrub is a stream of
    // positions, and each seek asked for while the last is still being answered is one too many. The newest
    // position waits here and is sent when the timer next fires; the ones in between are never sent at all.
    private readonly DispatcherTimer _seekPace = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private long _seekWanted = -1, _seekSent = -1;
    private readonly CancellationTokenSource _starting = new();

    private string _previewFile = "";
    private int _previewRun;
    private bool _showingCue;
    private bool _edited;

    public SubtitleEditorWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _images = viewModel.SubtitlesAreImages;

        Timeline.Cues = viewModel.SubtitleCues;
        Timeline.CuesAreImages = _images;
        Timeline.OffsetSeconds = OffsetSeconds;
        Timeline.DurationSeconds = Math.Max(viewModel.DurationMs / 1000, viewModel.SubtitleCues.Select(c => c.End + OffsetSeconds).DefaultIfEmpty(0).Max() + 5);
        Timeline.PositionSeconds = viewModel.PositionMs / 1000;

        Timeline.Scrubbed += seconds => MoveTo(seconds, fromTimeline: true);
        Timeline.CueDragged += _ =>
        {
            _edited = true;
            ShowCue(Timeline.Selected);
            _previewRefresh.Start();
            _mainSync.Stop();
            _mainSync.Start();
        };
        Timeline.CueReleased += _ => RefreshPreviewNow();
        Timeline.SelectionChanged += ShowCue;
        Timeline.ActiveWordChanged += () => DeleteButton.Content = Timeline.ActiveWord >= 0 ? "Delete Word" : "Delete";

        // The same things, on a right-click of the timeline: about the subtitle (and the word) under the pointer.
        var menu = new ContextMenu();
        void Item(string header, RoutedEventHandler action, Func<bool> enabled)
        {
            var item = new MenuItem { Header = header };
            item.Click += action;
            menu.Opened += (_, _) => item.IsEnabled = enabled();
            menu.Items.Add(item);
        }

        Item("Add Segment at Playhead", Add_Click, () => !_images);
        Item("Split at Playhead", Split_Click, () => !_images && Timeline.Selected is not null);
        Item("Add Word", AddWord_Click, () => !_images && Timeline.Selected is not null);
        Item("Delete Word", Delete_Click, () => !_images && Timeline is { Selected: not null, ActiveWord: >= 0 });
        Item("Delete Segment", DeleteSegment_Click, () => !_images && Timeline.Selected is not null);
        Timeline.ContextMenu = menu;

        _mainSync.Tick += (_, _) =>
        {
            // The playhead has rested: the main window follows, and takes the cues as they now are.
            _mainSync.Stop();
            if (_edited)
            {
                _edited = false;
                _viewModel.CommitSubtitleEdits();
            }

            if (_viewModel.DurationMs > 0)
                _viewModel.PositionMs = Math.Clamp(Timeline.PositionSeconds * 1000, 0, _viewModel.DurationMs);
        };
        _previewRefresh.Tick += (_, _) => RefreshPreviewNow();
        _playCheck.Tick += PlayCheck_Tick;
        _seekPace.Tick += (_, _) =>
        {
            if (_seekWanted == _seekSent)
                _seekPace.Stop();
            else
                SendSeek();
        };

        // A picture track is retimed by writing its whole file out again, which is not done sixteen times a second.
        if (_images)
            _previewRefresh.Interval = TimeSpan.FromMilliseconds(400);

        OffsetBox.Text = _viewModel.SubtitleOffsetMs.ToString("0", CultureInfo.InvariantCulture);
        (AddButton.IsEnabled, DeleteButton.IsEnabled) = (!_images, false);
        ShowCue(null);
        HintText.Text = _images
            ? "An image-based track: its pictures cannot be edited as text. Drag a subtitle, or one of its ends, to retime it."
            : "Drag the strip to scrub and use the mouse wheel to zoom. Drag a subtitle to move it, or one of its ends to retime that end.";
        UpdateSummary();
        UpdateTime();

        Surface.SurfaceReady += () => _ = StartPlayerAsync();
        Loaded += (_, _) => _ = StartPlayerAsync();
        Closed += (_, _) => Shutdown();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private double OffsetSeconds => double.IsFinite(_viewModel.SubtitleOffsetMs) ? _viewModel.SubtitleOffsetMs / 1000 : 0;

    // ----- The player -----

    private async Task StartPlayerAsync()
    {
        if (_player is not null || _closed || Surface.SurfaceHandle == IntPtr.Zero)
            return;

        if (!MpvPlayer.IsInstalled)
        {
            PlayerNotice.Text = "The player (libmpv) is not installed, so there is no preview here. The timeline still works.";
            PlayerNotice.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var surface = Surface.SurfaceHandle;
            var player = await Task.Run(() => MpvPlayer.Create(surface, AppSettings.Current.PlayerHardwareAcceleration));
            if (_closed || _player is not null)
            {
                player.Close();
                return;
            }

            _player = player;
            player.TimeChanged += time => Dispatcher.BeginInvoke(() => OnPlayerTime(time));
            player.StateChanged += () => Dispatcher.BeginInvoke(() =>
            {
                PlayButton.Content = player.IsPlaying ? "" : "";

                // Stopped by itself at the end: that is then the state in hand. (An "end" anywhere else is the
                // player's passing mistake, which the check after a click puts right.)
                if (player.IsEnded && AtEnd)
                    _wantPlaying = false;
            });

            // The blank picture is made by the player itself. Should this libmpv not be able to, the reason is
            // shown in place of the picture (the player's own window would otherwise cover it); the timeline still works.
            var opened = false;
            player.FileLoaded += () => opened = true;
            player.ErrorLogged += message => Dispatcher.BeginInvoke(() =>
            {
                if (opened || _closed)
                    return;

                Surface.Visibility = Visibility.Collapsed;
                PlayerNotice.Text = $"The preview could not be started: {message}";
                PlayerNotice.Visibility = Visibility.Visible;
            });
            player.SetVolume(0);
            player.FileLoaded += () => Dispatcher.BeginInvoke(() =>
            {
                // The file is open: the subtitles go onto it, the playhead may have moved meanwhile, and Play may
                // have been pressed while it was still opening.
                RefreshPreviewNow();
                ApplyPlaying();
                if (_seekWanted >= 0 && _seekWanted != _seekSent)
                    SendSeek();
            });

            // A blank video in the shape of the output frame, as long as the timeline: all that is ever seen on it
            // is the subtitles. A file, so that the player can be sent to any moment of it.
            if (await _viewModel.PrepareSubtitleCanvasAsync(Timeline.DurationSeconds, _starting.Token) is not { } canvas)
            {
                Surface.Visibility = Visibility.Collapsed;
                PlayerNotice.Text = "The preview could not be started: the blank video it plays could not be made (see HandPeg.log). The timeline still works.";
                PlayerNotice.Visibility = Visibility.Visible;
                return;
            }

            if (_closed)
                return;

            _seekSent = (long)(Timeline.PositionSeconds * 1000);
            player.Open(canvas, _seekSent, paused: true);
            RefreshPreviewNow();
        }
        catch (OperationCanceledException)
        {
            // Closed while it was starting.
        }
        catch (InvalidOperationException ex)
        {
            PlayerNotice.Text = $"The preview could not be started: {ex.Message}";
            PlayerNotice.Visibility = Visibility.Visible;
        }
    }

    private void OnPlayerTime(long milliseconds)
    {
        // While it plays the timeline follows it; paused, the timeline is what moves the player.
        if (_player is not { IsPlaying: true })
            return;

        // In the moment after Play is pressed the player may name a time far from where it was started (the
        // end of the file, in passing): the playhead does not follow it there.
        var (seconds, since) = (milliseconds / 1000.0, (DateTime.UtcNow - _playAsked).TotalSeconds);
        if (since < 1.5 && Math.Abs(seconds - _playFrom) > since + 1.0)
            return;

        Timeline.PositionSeconds = seconds;
        UpdateTime();
        _mainSync.Stop();
        _mainSync.Start();
    }

    /// <summary>Moves the playhead: the editor's own player at once, the main window when it has rested.</summary>
    private void MoveTo(double seconds, bool fromTimeline = false)
    {
        if (!fromTimeline)
            Timeline.PositionSeconds = seconds;
        _seekWanted = (long)Math.Max(Timeline.PositionSeconds * 1000, 0);
        if (!_seekPace.IsEnabled)
        {
            // The first of a run goes at once; the rest are paced.
            SendSeek();
            _seekPace.Start();
        }

        UpdateTime();
        _mainSync.Stop();
        _mainSync.Start();
    }

    private void SendSeek()
    {
        // Not while the file is still being opened: it opens at the position it was given, and a seek sent
        // into a file that is not there yet is what leaves a player with nothing to show.
        if (_player is not { IsLoading: false } player || _closed)
            return;

        _seekSent = _seekWanted;
        player.Seek(_seekSent, exact: true);
    }

    /// <summary>Writes the track as it is now and has the editor's player draw it. A new file each time: the player loads a file again only when its name has changed.</summary>
    private void RefreshPreviewNow()
    {
        _previewRefresh.Stop();
        if (_player is not { } player)
            return;

        try
        {
            var path = _viewModel.WriteSubtitlePreview($"subtitle_editor_{++_previewRun}") ?? "";
            player.SetSubtitleFile(path);

            // The player read the earlier file whole when it was given it, so that one can go; an imported file is never one of these.
            var before = _previewFile;
            _previewFile = path;
            if (before.Length > 0 && before != path && Path.GetFileName(before).StartsWith("subtitle", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(before, _viewModel.SubtitleSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(before);
                if (Path.GetExtension(before).Equals(".idx", StringComparison.OrdinalIgnoreCase))
                    File.Delete(Path.ChangeExtension(before, ".sub"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The picture is a moment behind; the next change writes it again.
        }
    }

    private void Shutdown()
    {
        _closed = true;
        _mainSync.Stop();
        _previewRefresh.Stop();
        _seekPace.Stop();
        _playCheck.Stop();
        _starting.Cancel();
        _player?.Close();
        _player = null;

        // What the debounce had not yet passed on.
        _viewModel.CommitSubtitleEdits();
        if (_viewModel.DurationMs > 0)
            _viewModel.PositionMs = Math.Clamp(Timeline.PositionSeconds * 1000, 0, _viewModel.DurationMs);
    }

    // ----- The selected cue -----

    private void ShowCue(SubtitleCue? cue)
    {
        _showingCue = true;
        try
        {
            CueText.Text = cue is null ? "" : _images ? "(image subtitle: no text to edit)" : cue.Text;
            StartBox.Text = cue is null ? "" : cue.Start.ToString("0.000", CultureInfo.InvariantCulture);
            EndBox.Text = cue is null ? "" : cue.End.ToString("0.000", CultureInfo.InvariantCulture);
            (CueText.IsEnabled, StartBox.IsEnabled, EndBox.IsEnabled) = (cue is not null && !_images, cue is not null, cue is not null);
            DeleteButton.IsEnabled = SplitButton.IsEnabled = AddWordButton.IsEnabled = cue is not null && !_images;
            DeleteButton.Content = "Delete";
        }
        finally
        {
            _showingCue = false;
        }
    }

    private void CueText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_showingCue || _images || Timeline.Selected is not { } cue || cue.Text == CueText.Text)
            return;

        cue.Text = CueText.Text;
        Changed();
    }

    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (ReferenceEquals(sender, OffsetBox))
            OffsetBox_LostFocus(sender, e);
        else
            TimeBox_LostFocus(sender, e);
        e.Handled = true;
    }

    private void TimeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Timeline.Selected is not { } cue)
            return;

        static double? Read(TextBox box) =>
            double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

        var (start, end) = (Read(StartBox) ?? cue.Start, Read(EndBox) ?? cue.End);
        start = Math.Max(start, -OffsetSeconds);
        end = Math.Max(end, start + 0.1);
        if (Math.Abs(start - cue.Start) > 0.0004 || Math.Abs(end - cue.End) > 0.0004)
        {
            (cue.Start, cue.End) = (Math.Round(start, 3), Math.Round(end, 3));
            Changed();
        }

        ShowCue(cue);
    }

    // The Global Offset is applied as it is typed: every cue moves on the timeline and in the preview with each digit.
    private void OffsetBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && double.TryParse(OffsetBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var offset) && double.IsFinite(offset))
            ApplyOffset(offset);
    }

    // Leaving the box tidies what was typed: something that is not a number goes back to the offset in use.
    private void OffsetBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var text = _viewModel.SubtitleOffsetMs.ToString("0", CultureInfo.InvariantCulture);
        if (OffsetBox.Text != text)
            OffsetBox.Text = text;
    }

    private void ApplyOffset(double offset)
    {
        offset = Math.Clamp(Math.Round(offset), -3_600_000, 3_600_000);
        if (Math.Abs(offset - _viewModel.SubtitleOffsetMs) < 0.5)
            return;

        _viewModel.SubtitleOffsetMs = offset;
        Timeline.OffsetSeconds = OffsetSeconds;
        Changed();
    }

    /// <summary>A cue changed: the timeline and the editor's player show it at once, the main window when things have rested.</summary>
    private void Changed()
    {
        _edited = true;
        Timeline.InvalidateVisual();
        _previewRefresh.Start();
        _mainSync.Stop();
        _mainSync.Start();
        UpdateSummary();
    }

    // ----- Buttons -----

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_player is not { } player)
            return;

        // What is wanted is kept here, and not read back from the player: its word on whether it is playing
        // arrives a moment after the fact (and after a run of seeks it can be a seek behind), and a click
        // decided on a stale word asked for the state the player was already in, which did nothing.
        _wantPlaying = !_wantPlaying;
        // Play with the playhead at the end of the timeline starts again from the beginning. (The editor opens
        // where the main window's playhead is, which is often the end; the player does not call itself
        // "ended" until it has been run into the end once, so asking it is not enough: started there, it
        // stopped at once, and Play seemed to do nothing until it was pressed a second time.)
        if (_wantPlaying && (player.IsEnded || AtEnd))
        {
            Timeline.PositionSeconds = 0;
            player.Seek(0, exact: true);
            UpdateTime();
        }

        (_playFrom, _playAsked) = (Timeline.PositionSeconds, DateTime.UtcNow);
        ApplyPlaying();
        _playCheck.Stop();
        _playChecks = 0;
        _playCheck.Start();
    }

    // Whether the playhead really is at the end of the timeline: only then is "ended" the end.
    private bool AtEnd => Timeline.PositionSeconds >= Timeline.DurationSeconds - 0.5;

    // Where the playhead was, and when, the last time Play was pressed.
    private double _playFrom;
    private DateTime _playAsked;

    private bool _wantPlaying;
    private int _playChecks;
    private readonly DispatcherTimer _playCheck = new() { Interval = TimeSpan.FromMilliseconds(180) };

    private void ApplyPlaying()
    {
        // A file still being opened is given the state when it is ready (see FileLoaded).
        if (_player is { IsLoading: false } player && !_closed)
            player.SetPause(!_wantPlaying);
    }

    // Looked at a few times after a click: should the player not have done as it was asked, it is asked again.
    private void PlayCheck_Tick(object? sender, EventArgs e)
    {
        if (_player is not { } player || _closed || ++_playChecks > 6 || player.IsPlaying == _wantPlaying || (_wantPlaying && player.IsEnded && AtEnd))
        {
            _playCheck.Stop();
            return;
        }

        // Started straight after a run of seeks, the player can report the end of the file for a moment and
        // stop itself there, though the playhead is nowhere near the end. It is put back where the playhead
        // was when Play was pressed, and started again.
        if (_wantPlaying)
            player.Seek((long)Math.Max(_playFrom * 1000, 0), exact: true);
        ApplyPlaying();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    private void Step(int direction)
    {
        var at = Timeline.PositionSeconds - OffsetSeconds;
        var cue = direction > 0
            ? _viewModel.SubtitleCues.Where(c => c.Start > at + 0.01).MinBy(c => c.Start)
            : _viewModel.SubtitleCues.Where(c => c.Start < at - 0.01).MaxBy(c => c.Start);
        if (cue is null)
            return;

        Timeline.Select(cue);
        MoveTo(cue.Start + OffsetSeconds);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_images)
            return;

        var start = Math.Max(Timeline.PositionSeconds - OffsetSeconds, 0);
        var cue = _viewModel.AddSubtitleCue(start, start + 2, "");
        Timeline.DurationSeconds = Math.Max(Timeline.DurationSeconds, cue.End + OffsetSeconds + 5);
        Timeline.Select(cue);
        Changed();
        CueText.Focus();
        CueText.SelectAll();
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_images || Timeline.Selected is not { } cue)
            return;

        if (_viewModel.SplitSubtitleCue(cue, Timeline.PositionSeconds - OffsetSeconds) is not { } second)
        {
            HintText.Text = "Split: put the playhead inside the selected subtitle first, where it is to be cut.";
            return;
        }

        Timeline.Select(second);
        Changed();
    }

    private void AddWord_Click(object sender, RoutedEventArgs e)
    {
        if (_images || Timeline.Selected is not { } cue)
            return;

        // The new word goes on the end, and is left selected in the box to be typed over.
        cue.Text = cue.Text.TrimEnd().Length > 0 ? cue.Text.TrimEnd() + " word" : "word";
        ShowCue(cue);
        Changed();
        CueText.Focus();
        CueText.Select(Math.Max(CueText.Text.Length - 4, 0), 4);
    }

    // Delete: the word that is picked out, when one is and it is not the cue's only word; the whole subtitle otherwise.
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_images || Timeline.Selected is not { } cue)
            return;

        if (Timeline.ActiveWord >= 0 && _viewModel.RemoveSubtitleWord(cue, Timeline.ActiveWord))
        {
            var kept = cue;
            Timeline.Select(null);
            Timeline.Select(kept);
            Changed();
            return;
        }

        DeleteSegment_Click(sender, e);
    }

    private void DeleteSegment_Click(object sender, RoutedEventArgs e)
    {
        if (_images || Timeline.Selected is not { } cue)
            return;

        _viewModel.RemoveSubtitleCue(cue);
        Timeline.Select(null);
        Changed();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Space plays and pauses, except where it is a letter being typed.
        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox)
        {
            Play_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox)
        {
            Delete_Click(sender, e);
            e.Handled = true;
        }
    }

    private void UpdateTime()
    {
        var time = TimeSpan.FromSeconds(Math.Max(Timeline.PositionSeconds, 0));
        TimeText.Text = string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}");
    }

    private void UpdateSummary() =>
        SummaryText.Text = $"{_viewModel.SubtitleCues.Count} subtitle{(_viewModel.SubtitleCues.Count == 1 ? "" : "s")}"
                           + (_viewModel.SubtitleSourcePath.Length > 0 ? $"  ·  {Path.GetFileName(_viewModel.SubtitleSourcePath)}" : "");
}
