using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp.Views;

/// <summary>
/// The Subtitle Editor: the subtitle track's cues on a timeline of their own, with a small preview that shows
/// nothing but the subtitles: a black picture the shape of the caption box with the cue that is up drawn on it by
/// WPF (<see cref="SubtitlePreview"/>, 5.0-Verify; no player), so retiming a cue is seen at once and costs none of
/// what the main window's player, with its layers, would. Play runs the editor's own clock. The main window is told
/// where the editor's playhead is only once it has rested for half a second, and draws no subtitles of its own
/// while the editor is open.
/// </summary>
public partial class SubtitleEditorWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly bool _images;

    private bool _closed;

    // Tells the main window where the playhead is: restarted by every move, so it fires half a second after the last.
    private readonly DispatcherTimer _mainSync = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // Gives the preview the cues again: at most every so often while a cue is dragged or typed in.
    private readonly DispatcherTimer _previewRefresh = new() { Interval = TimeSpan.FromMilliseconds(60) };

    // The editor's clock while it plays: the playhead is where Play was pressed plus the time since (each frame drawn).
    private readonly Stopwatch _clock = new();
    private double _playFrom;
    private bool _playing;

    private bool _showingCue;
    private bool _filling;
    private bool _edited;

    public SubtitleEditorWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        _viewModel = viewModel;
        _images = viewModel.SubtitlesAreImages;

        Timeline.Cues = viewModel.SubtitleCues;
        Timeline.CuesAreImages = _images;
        Timeline.OffsetSeconds = OffsetSeconds;
        Timeline.DurationSeconds = Math.Max(viewModel.DurationMs / 1000, viewModel.SubtitleCues.Select(c => c.End + OffsetSeconds).DefaultIfEmpty(0).Max() + 5);
        Timeline.PositionSeconds = viewModel.PositionMs / 1000;

        Timeline.Scrubbed += seconds => MoveTo(seconds, fromTimeline: true);
        Timeline.CueGrabbed += _ => Remember();
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

        Item("Add Caption at Playhead", Add_Click, () => !_images);
        Item("Split at Playhead", Split_Click, () => !_images && Timeline.Selected is not null);
        Item("Add Word", AddWord_Click, () => !_images && Timeline.Selected is not null);
        Item("Delete Word", Delete_Click, () => !_images && Timeline is { Selected: not null, ActiveWord: >= 0 });
        Item("Delete Caption", DeleteSegment_Click, () => !_images && Timeline.Selected is not null);
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

        Fill(OffsetBox, _viewModel.SubtitleOffsetMs.ToString("0", CultureInfo.InvariantCulture));
        (AddButton.IsEnabled, DeleteButton.IsEnabled) = (!_images, false);
        ShowCue(null);
        UpdateUndoButtons();
        HintText.Text = _images
            ? "An image-based track: its pictures cannot be edited as text. Drag a subtitle, or one of its ends, to retime it."
            : "Drag the strip to scrub and use the mouse wheel to zoom. Drag a subtitle to move it, or one of its ends to retime that end.";
        UpdateSummary();
        UpdateTime();

        RefreshPreviewNow();
        Closed += (_, _) => Shutdown();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private double OffsetSeconds => double.IsFinite(_viewModel.SubtitleOffsetMs) ? _viewModel.SubtitleOffsetMs / 1000 : 0;

    // ----- The preview -----

    // While it plays, each frame drawn moves the playhead on by the clock; the end of the timeline stops it there.
    private void OnRendering(object? sender, EventArgs e)
    {
        var seconds = _playFrom + _clock.Elapsed.TotalSeconds;
        if (seconds >= Timeline.DurationSeconds)
        {
            seconds = Timeline.DurationSeconds;
            SetPlaying(false);
        }

        Timeline.PositionSeconds = seconds;
        Preview.Time = seconds;
        UpdateTime();
        _mainSync.Stop();
        _mainSync.Start();
    }

    private void SetPlaying(bool playing)
    {
        if (playing == _playing || (playing && _closed))
            return;

        _playing = playing;
        if (playing)
        {
            _playFrom = Timeline.PositionSeconds;
            _clock.Restart();
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            _clock.Stop();
            CompositionTarget.Rendering -= OnRendering;
        }

        PlayButton.Content = playing ? "\uE769" : "\uE768";
    }

    /// <summary>Moves the playhead: the editor's preview at once, the main window when it has rested.</summary>
    private void MoveTo(double seconds, bool fromTimeline = false)
    {
        if (!fromTimeline)
            Timeline.PositionSeconds = seconds;

        // Playing, the clock runs on from the new moment.
        if (_playing)
        {
            _playFrom = Timeline.PositionSeconds;
            _clock.Restart();
        }

        Preview.Time = Timeline.PositionSeconds;
        UpdateTime();
        _mainSync.Stop();
        _mainSync.Start();
    }

    /// <summary>Gives the preview the track as it is now.</summary>
    private void RefreshPreviewNow()
    {
        _previewRefresh.Stop();
        if (_closed)
            return;

        var box = _viewModel.SubtitleFrameSize;
        if (_images)
        {
            Preview.ShowPictures(_viewModel.SubtitleCues.Select(c => (c.Start + OffsetSeconds, c.End + OffsetSeconds)).OrderBy(c => c.Item1).ToList(), box);
        }
        else
        {
            var (lines, style, textScale) = _viewModel.SubtitleEditorCaptions();
            Preview.ShowCaptions(lines, style, textScale, box);
        }

        Preview.Time = Timeline.PositionSeconds;
    }

    private void Shutdown()
    {
        SetPlaying(false);
        _closed = true;
        _mainSync.Stop();
        _previewRefresh.Stop();

        // What the debounce had not yet passed on.
        _viewModel.CommitSubtitleEdits();
        if (_viewModel.DurationMs > 0)
            _viewModel.PositionMs = Math.Clamp(Timeline.PositionSeconds * 1000, 0, _viewModel.DurationMs);
    }

    // ----- The selected cue -----

    private void ShowCue(SubtitleCue? cue)
    {
        // The boxes are filled afresh, not typed into: a run of typing into the cue's words ends here.
        if (_typing.Box == Typing.CueText)
            _typing = default;

        _showingCue = true;
        try
        {
            Fill(CueText, cue is null ? "" : _images ? "(image subtitle: no text to edit)" : cue.Text);
            Fill(StartBox, cue is null ? "" : cue.Start.ToString("0.000", CultureInfo.InvariantCulture));
            Fill(EndBox, cue is null ? "" : cue.End.ToString("0.000", CultureInfo.InvariantCulture));
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
        if (_showingCue || _filling || _images || Timeline.Selected is not { } cue || cue.Text == CueText.Text)
            return;

        Remember(Typing.CueText, cue);
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
            Remember();
            (cue.Start, cue.End) = (Math.Round(start, 3), Math.Round(end, 3));
            Changed();
        }

        ShowCue(cue);
    }

    // The Global Offset is applied as it is typed: every cue moves on the timeline and in the preview with each digit.
    private void OffsetBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && !_filling && double.TryParse(OffsetBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var offset) && double.IsFinite(offset))
            ApplyOffset(offset);
    }

    // Leaving the box (or Enter) tidies what was typed: something that is not a number goes back to the offset in use. The
    // run of typing ends there too: what it changed is a step of the editor's Undo from then on, no longer the box's own.
    private void OffsetBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var text = _viewModel.SubtitleOffsetMs.ToString("0", CultureInfo.InvariantCulture);
        if (OffsetBox.Text != text)
            Fill(OffsetBox, text);

        if (_typing.Box == Typing.Offset)
        {
            _typing = default;
            Forget(OffsetBox);
        }
    }

    private void ApplyOffset(double offset)
    {
        offset = Math.Clamp(Math.Round(offset), -3_600_000, 3_600_000);
        if (Math.Abs(offset - _viewModel.SubtitleOffsetMs) < 0.5)
            return;

        Remember(Typing.Offset);
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
        // Play with the playhead at the end of the timeline starts again from the beginning (the editor opens where the
        // main window's playhead is, which is often the end).
        if (!_playing && AtEnd)
            MoveTo(0);
        SetPlaying(!_playing);
    }

    // Whether the playhead is at the end of the timeline, where Play starts again from the beginning.
    private bool AtEnd => Timeline.PositionSeconds >= Timeline.DurationSeconds - 0.5;

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
        Remember();
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

        var before = Capture();
        if (_viewModel.SplitSubtitleCue(cue, Timeline.PositionSeconds - OffsetSeconds) is not { } second)
        {
            HintText.Text = "Split: put the playhead inside the selected subtitle first, where it is to be cut.";
            return;
        }

        Keep(before);
        Timeline.Select(second);
        Changed();
    }

    private void AddWord_Click(object sender, RoutedEventArgs e)
    {
        if (_images || Timeline.Selected is not { } cue)
            return;

        // The new word goes on the end, and is left selected in the box to be typed over.
        Remember();
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

        if (Timeline.ActiveWord >= 0 && Capture() is var before && _viewModel.RemoveSubtitleWord(cue, Timeline.ActiveWord))
        {
            Keep(before);
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
        if (_images || Timeline.Selected is not { } cue || !_viewModel.SubtitleCues.Contains(cue))
            return;

        Remember();
        _viewModel.RemoveSubtitleCue(cue);
        Timeline.Select(null);
        Changed();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Undo: Ctrl+Z. Redo: Ctrl+Y or Ctrl+Shift+Z. A text box takes back (or puts back) its own typing first; past
        // that, and everywhere else, they are the editor's steps.
        var undo = e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control;
        var redo = (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control) || (e.Key == Key.Z && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
        if (undo || redo)
        {
            if (Keyboard.FocusedElement is TextBox box && (redo ? box.CanRedo : box.CanUndo))
            {
                if (redo)
                    box.Redo();
                else
                    box.Undo();
            }
            else if (redo)
            {
                Redo_Click(sender, e);
            }
            else
            {
                Undo_Click(sender, e);
            }

            e.Handled = true;
            return;
        }

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

    // ----- Undo and redo (4.3E) -----
    // The editor keeps an undo of its own over the cues it shows: before each change, the cues (with the offset, and which
    // was selected) as they were. A run of typing into one cue's words, or into the offset box, is one step however many
    // keys it takes; a drag is one step, kept when the cue first moves; a time typed in, Add, Split, Add Word and each
    // Delete are one step each. The editor is modal, so the main window's Undo can not be reached while it is open, and
    // takes back the whole visit as one step once it is closed (MainViewModel.CheckpointSubtitles): the two never fight.
    //
    // A text box has an undo of its own, for its typing. It holds only typing newer than the editor's last step: a box
    // filled from a cue starts with none, and every other change lets go of what the boxes held. So Ctrl+Z in a box takes
    // back its typing piece by piece and then, with nothing left there, the editor's steps.

    private const int UndoDepth = 50;
    private readonly List<CueSnapshot> _undo = [], _redo = [];

    // The run of typing whose first key took the newest step: which box, and (for the words) which cue.
    private (Typing Box, SubtitleCue? Cue) _typing;

    private enum Typing { None, CueText, Offset }

    /// <summary>
    /// The track as a step of the editor's Undo keeps it: every cue (with the piece of an anchored caption it shows, 4.4, so
    /// an undone edit is written back through the same clip), the offset, and which cue was selected (-1 for none).
    /// </summary>
    private sealed record CueSnapshot(List<SubtitleCueState> Cues, double OffsetMs, int Selected);

    private CueSnapshot Capture()
    {
        var cues = _viewModel.SubtitleCues;
        return new CueSnapshot([.. cues.Select(c => new SubtitleCueState(c.Start, c.End, c.Text, c.WordBreaks) { Piece = c.Piece })], _viewModel.SubtitleOffsetMs,
            Timeline.Selected is { } selected ? cues.IndexOf(selected) : -1);
    }

    /// <summary>Keeps the cues as they are now as a step, before a change. A run of typing is one step: only its first key takes one.</summary>
    private void Remember(Typing typing = Typing.None, SubtitleCue? cue = null)
    {
        if (typing != Typing.None && _typing.Box == typing && ReferenceEquals(_typing.Cue, cue))
            return;

        // The box being typed into keeps the key that took the step (and is never touched from inside its own TextChanged).
        Keep(Capture(), typing switch { Typing.CueText => CueText, Typing.Offset => OffsetBox, _ => null });
        _typing = (typing, cue);
    }

    /// <summary>
    /// Keeps a state taken before a change as the newest step: the steps undone before it are gone, and so is what the text
    /// boxes held (but the one being typed into).
    /// </summary>
    private void Keep(CueSnapshot before, TextBox? typedInto = null)
    {
        _undo.Add(before);
        if (_undo.Count > UndoDepth)
            _undo.RemoveAt(0);
        _redo.Clear();
        _typing = default;
        ForgetTyping(typedInto);
        UpdateUndoButtons();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Step(_undo, _redo);

    private void Redo_Click(object sender, RoutedEventArgs e) => Step(_redo, _undo);

    /// <summary>One step back (or forward again): the cues as the step kept them, and the cues as they are kept to come back to.</summary>
    private void Step(List<CueSnapshot> from, List<CueSnapshot> to)
    {
        // Not while a cue is held: the drag would go on moving it from where it was.
        if (Timeline.IsMouseCaptured)
            return;

        // A step that would change nothing (typing already taken back in its own box) is passed over.
        var now = Capture();
        while (from.Count > 0 && Same(from[^1], now))
            from.RemoveAt(from.Count - 1);

        if (from.Count > 0)
        {
            var state = from[^1];
            from.RemoveAt(from.Count - 1);
            to.Add(now);
            if (to.Count > UndoDepth)
                to.RemoveAt(0);

            _typing = default;
            ForgetTyping(null);
            Put(state);
        }

        UpdateUndoButtons();
    }

    /// <summary>The cues, the offset and the selection as a step kept them, shown at once and committed when things have rested, as any change is.</summary>
    private void Put(CueSnapshot state)
    {
        _viewModel.RestoreSubtitleCues(state.Cues, state.OffsetMs);
        Timeline.OffsetSeconds = OffsetSeconds;
        Fill(OffsetBox, _viewModel.SubtitleOffsetMs.ToString("0", CultureInfo.InvariantCulture));
        var cues = _viewModel.SubtitleCues;
        Timeline.DurationSeconds = Math.Max(Timeline.DurationSeconds, cues.Select(c => c.End + OffsetSeconds).DefaultIfEmpty(0).Max() + 5);

        // Chosen afresh: the cue that was selected may be another cue object now, holding its values.
        Timeline.Select(null);
        Timeline.Select(state.Selected >= 0 && state.Selected < cues.Count ? cues[state.Selected] : null);
        ShowCue(Timeline.Selected);
        Changed();
    }

    private static bool Same(CueSnapshot first, CueSnapshot second) =>
        first.OffsetMs.Equals(second.OffsetMs) && first.Cues.Count == second.Cues.Count
        && first.Cues.Zip(second.Cues).All(pair => pair.First.Start.Equals(pair.Second.Start) && pair.First.End.Equals(pair.Second.End)
                                                   && pair.First.Text == pair.Second.Text && (pair.First.Breaks ?? []).SequenceEqual(pair.Second.Breaks ?? []));

    private void UpdateUndoButtons() => (UndoButton.IsEnabled, RedoButton.IsEnabled) = (_undo.Count > 0, _redo.Count > 0);

    /// <summary>Lets go of what the text boxes could take back of their own (all but the one given): their undo never reaches past the editor's newest step.</summary>
    private void ForgetTyping(TextBox? keep)
    {
        foreach (var box in new[] { CueText, StartBox, EndBox, OffsetBox })
        {
            if (!ReferenceEquals(box, keep) && (box.CanUndo || box.CanRedo))
                Forget(box);
        }
    }

    // A text box's undo is emptied by switching it off; it is switched on again at once.
    private static void Forget(TextBox box)
    {
        box.IsUndoEnabled = false;
        box.IsUndoEnabled = true;
    }

    /// <summary>
    /// Puts text into a box from the cues, not as typing: the box's own undo is emptied, so Ctrl+Z there never brings back
    /// another cue's words, and the text is not taken as an edit.
    /// </summary>
    private void Fill(TextBox box, string text)
    {
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
