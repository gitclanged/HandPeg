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

/// <summary>Host services for the Subtitle Studio, which owns no player, clock or undo history.</summary>
public interface ISubtitleStudioHost
{
    /// <summary>Plays or pauses the main player (as Space does).</summary>
    void TogglePlay();

    /// <summary>Main window Undo (or Redo when <paramref name="redo"/>): one step of the shared history.</summary>
    void Undo(bool redo);

    /// <summary>Cues committed as a main-history step: redraw the timeline rows.</summary>
    void SubtitlesCommitted();
}

/// <summary>
/// Subtitle Studio dock pane: the subtitle track's cues on a dedicated strip (<see cref="SubtitleTimeline"/>), a transport, and the
/// selected cue's words and times. Non-modal, so the main player keeps rendering captions during edits and the main Undo stays live.
/// No own clock: the strip playhead follows PositionMs while visible (strip scrubs seek the player; a coasting strip is not
/// overridden). No own undo: each gesture commits via MainViewModel.CommitSubtitleEdits as one main-history step (drag release,
/// debounced typing, typed time, Add, Split, Add Word, each Delete); Undo/Redo buttons forward to the main window. Delete and Back
/// act on the active cue or word while the pane has focus (<see cref="IOwnsKeys"/>); Space stays with the main player.
/// Dock rebuilds detach and re-attach the pane, so it subscribes only while loaded and Attach is idempotent.
/// </summary>
public partial class SubtitleStudioPane : UserControl, IOwnsKeys
{
    /// <summary>Typing idle time before a commit, in milliseconds.</summary>
    internal const int CommitAfterMs = 600;

    private readonly MainViewModel _viewModel;
    private readonly ISubtitleStudioHost _host;

    // Debounced typing commit; restarted on every key.
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

        // Strip presses focus the pane (the strip is not focusable) so Delete targets the cue.
        Timeline.PreviewMouseDown += (_, _) => Focus();

        // Strip context menu: the same commands for the subtitle (and word) under the pointer.
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

    // ----- Subscriptions (while loaded) -----

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

        // Commit any pending debounced edit as a step.
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

    /// <summary>Coalesces cue changes (Undo restores cue by cue) into one Background-priority refresh.</summary>
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
    /// Syncs the pane to the track: image mode, offset and length, the selection (re-resolved when a restored step holds new cue
    /// objects), the edit boxes (never the focused one), the summary and the hint.
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
            // Re-resolve by equal values, else (Undo of its text or times) the cue spanning its midpoint, else the nearest start within
            // 0.5 s, so Undo keeps the working cue selected.
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

    // ----- Playhead (driven by the main player) -----

    /// <summary>Syncs the strip playhead to the player while visible; skipped during a strip drag or post-flick coast.</summary>
    private void FollowPlayhead()
    {
        if (!_attached || !IsVisible || Timeline.IsMouseCaptured || Timeline.IsCoasting)
            return;

        Timeline.PositionSeconds = _viewModel.PositionMs / 1000;
        UpdateTime();
    }

    /// <summary>Seeks the main player to a strip time (setting PositionMs moves the timeline, which seeks the player).</summary>
    private void Seek(double seconds)
    {
        if (_viewModel.DurationMs > 0)
            _viewModel.PositionMs = Math.Clamp(seconds * 1000, 0, _viewModel.DurationMs);
    }

    /// <summary>Moves both the strip playhead and the player to a time.</summary>
    private void MoveTo(double seconds)
    {
        Timeline.PositionSeconds = seconds;
        UpdateTime();
        Seek(Timeline.PositionSeconds);
    }

    private void UpdatePlayButton() => PlayButton.Content = _viewModel.IsPlaying ? Glyphs.Pause : Glyphs.Play;

    // ----- Commits (one main-history step per gesture) -----

    /// <summary>Marks an in-place cue change: redraws the strip now and restarts the debounced commit.</summary>
    private void Changed()
    {
        _edited = true;
        Timeline.InvalidateVisual();
        _commitSoon.Stop();
        _commitSoon.Start();
        UpdateSummary();
    }

    /// <summary>Commits in-place changes as one step (a no-op when unchanged; MainViewModel compares).</summary>
    private void Commit()
    {
        _commitSoon.Stop();
        _edited = false;
        _viewModel.CommitSubtitleEdits();
        _host.SubtitlesCommitted();
    }

    /// <summary>Commits any pending in-place change before another gesture records its own step.</summary>
    private void FlushEdits()
    {
        if (_edited)
            Commit();
        else
            _commitSoon.Stop();
    }

    // ----- Selected cue -----

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

    // Losing focus commits the box's typing so the next gesture gets its own step.
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

    // Global Offset applies live per keystroke (strip and player update immediately); committed as one debounced step.
    private void OffsetBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_attached && !_filling && double.TryParse(OffsetBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var offset) && double.IsFinite(offset))
            ApplyOffset(offset);
    }

    // On blur or Enter: normalize the text (non-numeric input reverts to the current offset) and commit.
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

        // Append a placeholder word and leave it selected for overtyping.
        FlushEdits();
        cue.Text = cue.Text.TrimEnd().Length > 0 ? cue.Text.TrimEnd() + " word" : "word";
        Commit();
        Fill(CueText, cue.Text);
        CueText.Focus();
        CueText.Select(Math.Max(CueText.Text.Length - 4, 0), 4);
    }

    // Deletes the active word when one is set and is not the cue's only word; otherwise deletes the whole subtitle.
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

    /// <summary>Claims unmodified Delete and Back for the active cue or word while focused, except inside a text box.</summary>
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
    /// Fills a box from the cue unless it holds uncommitted typing (newer than the model); committed or undone boxes show the current
    /// cue.
    /// </summary>
    private void FillUnlessTyping(TextBox box, string text)
    {
        if (!(_edited && box.IsKeyboardFocusWithin))
            Fill(box, text);
    }

    /// <summary>
    /// Sets box text programmatically (not as an edit): clears the box's local undo so Ctrl+Z never restores another cue's text.
    /// Unchanged text is left as is, preserving the caret.
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
