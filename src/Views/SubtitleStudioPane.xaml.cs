using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.ViewModels;

namespace HandPegApp.Views;

/// <summary>What the Subtitle Studio asks of the window it serves (5.4C): it has no player, no clock and no undo of its own.</summary>
public interface ISubtitleStudioHost
{
    /// <summary>Plays or pauses the main player (as Space does).</summary>
    void TogglePlay();

    /// <summary>The main window's Undo, or Redo: one step of the one history.</summary>
    void Undo(bool redo);

    /// <summary>The cues were committed (a step of the main history): the timeline's rows are drawn again.</summary>
    void SubtitlesCommitted();
}

/// <summary>
/// The Subtitle Studio as a dock pane (5.4C; a modal window of its own until then): the subtitle track's cues on a strip of
/// their own (<see cref="SubtitleTimeline"/>, hosted as it is), its transport, and the selected cue's words and times. It is not
/// modal (5.4D: the modal editor's flag is gone from the view model), so the main player goes on drawing the captions as they are
/// edited and the main Undo always works. It has no clock: its playhead follows the main player's (PositionMs, while the pane is in sight; a
/// scrub of the strip moves the player, and a coasting strip is left alone). It has no undo of its own: each gesture commits through
/// MainViewModel.CommitSubtitleEdits as one step of the main history (a drag when it is let go, a run of typing once it rests, a
/// time typed in, Add, Split, Add Word and each Delete), and its Undo and Redo buttons are the main window's. Delete and Back are
/// its own while the keyboard is in it (<see cref="IOwnsKeys"/>), for the cue or the word picked out; Space is the main player's.
/// Every dock rebuild takes the pane out and puts it back, so it listens only while loaded, and attaching twice does nothing.
/// </summary>
public partial class SubtitleStudioPane : UserControl, IOwnsKeys
{
    /// <summary>How long typing rests before it is committed as a step, milliseconds.</summary>
    internal const int CommitAfterMs = 600;

    private readonly MainViewModel _viewModel;
    private readonly ISubtitleStudioHost _host;

    // Commits a run of typing once it has rested; restarted by every key.
    private readonly DispatcherTimer _commitSoon = new() { Interval = TimeSpan.FromMilliseconds(CommitAfterMs) };

    private bool _attached, _images, _showingCue, _filling, _edited, _refreshQueued;

    public SubtitleStudioPane(MainViewModel viewModel, ISubtitleStudioHost host)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _host = host;

        Timeline.Scrubbed += seconds =>
        {
            UpdateTime();
            Seek(seconds);
        };
        Timeline.CueGrabbed += _ => FlushEdits();
        Timeline.CueDragged += _ =>
        {
            _edited = true;
            ShowCue(Timeline.Selected);
        };
        Timeline.CueReleased += _ =>
        {
            if (_edited)
                Commit();
        };
        Timeline.SelectionChanged += cue =>
        {
            FlushEdits();
            ShowCue(cue);
        };
        Timeline.ActiveWordChanged += () => DeleteButton.Content = Timeline.ActiveWord >= 0 ? "Delete Word" : "Delete";

        // A press on the strip gives the pane the keyboard, so Delete is the cue's (the strip itself takes no focus).
        Timeline.PreviewMouseDown += (_, _) => Focus();

        // The same things, on a right-click of the strip: about the subtitle (and the word) under the pointer.
        var menu = new ContextMenu();
        void Item(string header, RoutedEventHandler action, Func<bool> enabled)
        {
            var item = new MenuItem { Header = header };
            item.Click += action;
            menu.Opened += (_, _) => item.IsEnabled = enabled();
            menu.Items.Add(item);
        }

        Item("Add Caption at Playhead", Add_Click, () => !_images);
        Item("Split at Playhead", Split_Click, () => !_images && Timeline.Selected is not null);
        Item("Add Word", AddWord_Click, () => !_images && Timeline.Selected is not null);
        Item("Delete Word", Delete_Click, () => !_images && Timeline is { Selected: not null, ActiveWord: >= 0 });
        Item("Delete Caption", DeleteSegment_Click, () => !_images && Timeline.Selected is not null);
        Timeline.ContextMenu = menu;

        _commitSoon.Tick += (_, _) => FlushEdits();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                FollowPlayhead();
        };
    }

    private double OffsetSeconds => double.IsFinite(_viewModel.SubtitleOffsetMs) ? _viewModel.SubtitleOffsetMs / 1000 : 0;

    // ----- Listening (while loaded) -----

    private void Attach()
    {
        if (_attached)
            return;

        _attached = true;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.SubtitleCues.CollectionChanged += Cues_CollectionChanged;
        _viewModel.TimelineChanged += RefreshSoon;
        _viewModel.TimelineRestored += RefreshSoon;
        Timeline.Cues = _viewModel.SubtitleCues;
        Refresh();
        FollowPlayhead();
        UpdatePlayButton();
    }

    private void Detach()
    {
        if (!_attached)
            return;

        // What the debounce had not yet committed is a step now.
        FlushEdits();
        _attached = false;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.SubtitleCues.CollectionChanged -= Cues_CollectionChanged;
        _viewModel.TimelineChanged -= RefreshSoon;
        _viewModel.TimelineRestored -= RefreshSoon;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.PositionMs):
                FollowPlayhead();
                break;
            case nameof(MainViewModel.IsPlaying):
                UpdatePlayButton();
                break;
            case nameof(MainViewModel.SubtitlesAreImages) or nameof(MainViewModel.SubtitleOffsetMs) or nameof(MainViewModel.DurationMs)
                or nameof(MainViewModel.HasSubtitleCues) or nameof(MainViewModel.SubtitleSourcePath):
                RefreshSoon();
                break;
        }
    }

    private void Cues_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshSoon();

    /// <summary>The cues changed somewhere (an undo puts them back cue by cue): shown again once, after the change.</summary>
    private void RefreshSoon()
    {
        if (_refreshQueued || !_attached)
            return;

        _refreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            if (_attached)
                Refresh();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// The track as it is now: whether it is pictures, its offset and length, the selected cue (found again when the step put back
    /// holds another object with its values, else none), the boxes (never one being typed into), the summary and the hint.
    /// </summary>
    private void Refresh()
    {
        var cues = _viewModel.SubtitleCues;
        _images = _viewModel.SubtitlesAreImages;
        Timeline.CuesAreImages = _images;
        Timeline.OffsetSeconds = OffsetSeconds;
        Timeline.DurationSeconds = Math.Max(_viewModel.DurationMs / 1000, cues.Select(c => c.End + OffsetSeconds).DefaultIfEmpty(0).Max() + 5);

        if (Timeline.Selected is { } selected && !cues.Contains(selected))
        {
            // Its values, else (an undo of its text or its times: 5.4C review) the cue over its middle, else the one starting nearest it
            // (within half a second), so an undo keeps the cue being worked on selected.
            var middle = (selected.Start + selected.End) / 2;
            var same = cues.FirstOrDefault(c => Math.Abs(c.Start - selected.Start) < 0.0005 && c.Text == selected.Text)
                       ?? cues.FirstOrDefault(c => c.Start <= middle && middle < c.End)
                       ?? cues.Where(c => Math.Abs(c.Start - selected.Start) < 0.5).MinBy(c => Math.Abs(c.Start - selected.Start));
            Timeline.Select(null);
            Timeline.Select(same);
        }

        ShowCue(Timeline.Selected);
        if (!OffsetBox.IsKeyboardFocusWithin)
            Fill(OffsetBox, _viewModel.SubtitleOffsetMs.ToString("0", CultureInfo.InvariantCulture));
        AddButton.IsEnabled = !_images;
        HintText.Text = _images
            ? "An image-based track: its pictures cannot be edited as text. Drag a subtitle, or one of its ends, to retime it."
            : "Drag the strip to scrub the player; the wheel or a sideways swipe scrolls, Ctrl + wheel or a pinch zooms. Drag a subtitle to move it, or one of its ends to retime that end. The player shows the captions.";
        UpdateSummary();
        UpdateTime();
        Timeline.InvalidateVisual();
    }

    // ----- The playhead: the main player's -----

    /// <summary>The strip follows the player while the pane is in sight; never under a drag of its own, nor while it coasts after a flick.</summary>
    private void FollowPlayhead()
    {
        if (!_attached || !IsVisible || Timeline.IsMouseCaptured || Timeline.IsCoasting)
            return;

        Timeline.PositionSeconds = _viewModel.PositionMs / 1000;
        UpdateTime();
    }

    /// <summary>Sends the main player to a moment of the strip (setting the position moves the timeline, which seeks the player).</summary>
    private void Seek(double seconds)
    {
        if (_viewModel.DurationMs > 0)
            _viewModel.PositionMs = Math.Clamp(seconds * 1000, 0, _viewModel.DurationMs);
    }

    /// <summary>Moves the strip's playhead and the player's to a moment.</summary>
    private void MoveTo(double seconds)
    {
        Timeline.PositionSeconds = seconds;
        UpdateTime();
        Seek(Timeline.PositionSeconds);
    }

    private void UpdatePlayButton() => PlayButton.Content = _viewModel.IsPlaying ? "" : "";

    // ----- Committing: each gesture one step of the main history -----

    /// <summary>The cues were changed in place: the strip shows it at once, the main history takes it once the typing rests.</summary>
    private void Changed()
    {
        _edited = true;
        Timeline.InvalidateVisual();
        _commitSoon.Stop();
        _commitSoon.Start();
        UpdateSummary();
    }

    /// <summary>Commits what was changed in place as one step (nothing when nothing was changed: MainViewModel compares).</summary>
    private void Commit()
    {
        _commitSoon.Stop();
        _edited = false;
        _viewModel.CommitSubtitleEdits();
        _host.SubtitlesCommitted();
    }

    /// <summary>A change made in place and not yet committed is committed now, before another gesture takes a step of its own.</summary>
    private void FlushEdits()
    {
        if (_edited)
            Commit();
        else
            _commitSoon.Stop();
    }

    // ----- The selected cue -----

    private void ShowCue(SubtitleCue? cue)
    {
        _showingCue = true;
        try
        {
            FillUnlessTyping(CueText, cue is null ? "" : _images ? "(image subtitle: no text to edit)" : cue.Text);
            FillUnlessTyping(StartBox, cue is null ? "" : cue.Start.ToString("0.000", CultureInfo.InvariantCulture));
            FillUnlessTyping(EndBox, cue is null ? "" : cue.End.ToString("0.000", CultureInfo.InvariantCulture));
            (CueText.IsEnabled, StartBox.IsEnabled, EndBox.IsEnabled) = (cue is not null && !_images, cue is not null, cue is not null);
            DeleteButton.IsEnabled = SplitButton.IsEnabled = AddWordButton.IsEnabled = cue is not null && !_images;
            DeleteButton.Content = Timeline.ActiveWord >= 0 && cue is not null ? "Delete Word" : "Delete";
        }
        finally
        {
            _showingCue = false;
        }
    }

    private void CueText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_showingCue || _filling || _images || Timeline.Selected is not { } cue || cue.Text == CueText.Text)
            return;

        cue.Text = CueText.Text;
        Changed();
    }

    // Leaving a box commits what was typed in it: the next gesture is a step of its own.
    private void EditBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => FlushEdits();

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
            FlushEdits();
            (cue.Start, cue.End) = (Math.Round(start, 3), Math.Round(end, 3));
            Commit();
        }

        Fill(StartBox, cue.Start.ToString("0.000", CultureInfo.InvariantCulture));
        Fill(EndBox, cue.End.ToString("0.000", CultureInfo.InvariantCulture));
    }

    // The Global Offset is applied as it is typed: every cue moves on the strip and in the player with each digit, one step once it rests.
    private void OffsetBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_attached && !_filling && double.TryParse(OffsetBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var offset) && double.IsFinite(offset))
            ApplyOffset(offset);
    }

    // Leaving the box (or Enter) tidies what was typed (something that is not a number goes back to the offset in use) and commits it.
    private void OffsetBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var text = _viewModel.SubtitleOffsetMs.ToString("0", CultureInfo.InvariantCulture);
        if (OffsetBox.Text != text)
            Fill(OffsetBox, text);
        FlushEdits();
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

    // ----- Buttons -----

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        FlushEdits();
        _host.TogglePlay();
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

        FlushEdits();
        var start = Math.Max(Timeline.PositionSeconds - OffsetSeconds, 0);
        var cue = _viewModel.AddSubtitleCue(start, start + 2, "");
        Commit();
        Timeline.DurationSeconds = Math.Max(Timeline.DurationSeconds, cue.End + OffsetSeconds + 5);
        Timeline.Select(cue);
        CueText.Focus();
        CueText.SelectAll();
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_images || Timeline.Selected is not { } cue)
            return;

        FlushEdits();
        if (_viewModel.SplitSubtitleCue(cue, Timeline.PositionSeconds - OffsetSeconds) is not { } second)
        {
            HintText.Text = "Split: put the playhead inside the selected subtitle first, where it is to be cut.";
            return;
        }

        Commit();
        Timeline.Select(second);
    }

    private void AddWord_Click(object sender, RoutedEventArgs e)
    {
        if (_images || Timeline.Selected is not { } cue)
            return;

        // The new word goes on the end, and is left selected in the box to be typed over.
        FlushEdits();
        cue.Text = cue.Text.TrimEnd().Length > 0 ? cue.Text.TrimEnd() + " word" : "word";
        Commit();
        Fill(CueText, cue.Text);
        CueText.Focus();
        CueText.Select(Math.Max(CueText.Text.Length - 4, 0), 4);
    }

    // Delete: the word that is picked out, when one is and it is not the cue's only word; the whole subtitle otherwise.
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_images || Timeline.Selected is not { } cue)
            return;

        FlushEdits();
        if (Timeline.ActiveWord >= 0 && _viewModel.RemoveSubtitleWord(cue, Timeline.ActiveWord))
        {
            Commit();
            Timeline.Select(null);
            Timeline.Select(cue);
            return;
        }

        DeleteSegment_Click(sender, e);
    }

    private void DeleteSegment_Click(object sender, RoutedEventArgs e)
    {
        if (_images || Timeline.Selected is not { } cue || !_viewModel.SubtitleCues.Contains(cue))
            return;

        FlushEdits();
        _viewModel.RemoveSubtitleCue(cue);
        Commit();
        Timeline.Select(null);
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        FlushEdits();
        _host.Undo(redo: false);
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        FlushEdits();
        _host.Undo(redo: true);
    }

    // ----- Keys -----

    /// <summary>Delete and Back are the studio's while the keyboard is in it (and not in one of its boxes, where they type): the cue's or the word's.</summary>
    public bool OwnsKey(Key key, ModifierKeys modifiers) =>
        key is Key.Delete or Key.Back && modifiers == ModifierKeys.None && Keyboard.FocusedElement is not TextBoxBase;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key is not (Key.Delete or Key.Back) || Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is TextBoxBase)
            return;

        Delete_Click(this, e);
        e.Handled = true;
    }

    // ----- Text -----

    private void UpdateTime()
    {
        var time = TimeSpan.FromSeconds(Math.Max(Timeline.PositionSeconds, 0));
        TimeText.Text = string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}");
    }

    private void UpdateSummary() =>
        SummaryText.Text = $"{_viewModel.SubtitleCues.Count} subtitle{(_viewModel.SubtitleCues.Count == 1 ? "" : "s")}"
                           + (_viewModel.SubtitleSourcePath.Length > 0 ? $"  ·  {Path.GetFileName(_viewModel.SubtitleSourcePath)}" : "");

    /// <summary>
    /// Fills a box from the cues unless typing in it is not yet committed (the box holds the newer text then); a box whose typing was
    /// committed, or taken back by the main Undo, shows the cue as it now is.
    /// </summary>
    private void FillUnlessTyping(TextBox box, string text)
    {
        if (!(_edited && box.IsKeyboardFocusWithin))
            Fill(box, text);
    }

    /// <summary>
    /// Puts text into a box from the cues, not as typing: the box's own undo is emptied, so Ctrl+Z there never brings back another
    /// cue's words, and the text is not taken as an edit. Text already there is left alone (the caret stays where it is).
    /// </summary>
    private void Fill(TextBox box, string text)
    {
        if (box.Text == text)
            return;

        _filling = true;
        try
        {
            box.IsUndoEnabled = false;
            box.Text = text;
            box.IsUndoEnabled = true;
        }
        finally
        {
            _filling = false;
        }
    }
}
