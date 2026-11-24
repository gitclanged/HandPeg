using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp.Views;

// ----- Color Studio LUT and Legacy Grade sections, and the post-grade YRGB histogram (pre-grade ghost) -----
public partial class ColorStudioPane
{
    /// <summary>Minimum interval between input-still captures, ms (~8 Hz, matching Video Scopes).</summary>
    internal const int ThrottleMs = 125;

    // Trailing throttle (created lazily): a change arms it, the tick captures; intermediate changes coalesce.
    private DispatcherTimer? _throttle;
    private bool _capturing, _wanted, _postWanted;
    private Border? _legacyCard;
    private TextBlock? _legacySummary;
    private Button? _legacyConvert;

    // LUT box text as last written by the pane; blur commits only when the text was edited.
    private string _lutShown = "";

    /// <summary>Number of input-still captures requested (goldens assert none while hidden or playing).</summary>
    internal int CaptureCount { get; private set; }

    // ----- LUT -----

    private StackPanel LutRows(EffectDefinition? definition)
    {
        var rows = new StackPanel();
        var file = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var clear = new Button { Content = "Clear", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0), ToolTip = "No LUT" };
        AutomationProperties.SetName(clear, "Clear LUT");
        clear.Click += (_, _) => QueueText(ColorGradeKeys.Lut, "");
        DockPanel.SetDock(clear, Dock.Right);
        var browse = new Button { Content = "Browse...", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Choose a .cube LUT file" };
        AutomationProperties.SetName(browse, "Browse LUT");
        browse.Click += (_, _) => BrowseLut();
        DockPanel.SetDock(browse, Dock.Right);
        _lutBox = new TextBox { VerticalAlignment = VerticalAlignment.Center, ToolTip = "A .cube LUT applied after the grade" };
        AutomationProperties.SetName(_lutBox, "LUT File");
        _lutBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                CommitLut();
        };
        _lutBox.LostKeyboardFocus += (_, _) => CommitLut();
        file.Children.Add(clear);
        file.Children.Add(browse);
        file.Children.Add(_lutBox);
        rows.Children.Add(file);
        if (definition?.Param(ColorGradeKeys.LutMix) is { } mix)
            rows.Children.Add(Row(mix));
        return rows;
    }

    // Commits only an edited path: leaving the box untouched writes nothing (so an undone path stays undone).
    private void CommitLut()
    {
        if (_lutBox.Text == _lutShown)
            return;
        var text = _lutBox.Text.Trim().Trim('"');
        if (text != (_card?.GetText(ColorGradeKeys.Lut) ?? ""))
            QueueText(ColorGradeKeys.Lut, text);
        _lutShown = _lutBox.Text;
    }

    private void BrowseLut()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Cube LUT (*.cube)|*.cube|All files (*.*)|*.*", Title = "Choose a LUT" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            QueueText(ColorGradeKeys.Lut, dialog.FileName);
    }

    // ----- Legacy Grade: former Filters tab sliders and LUT, shown while the project still uses them -----

    private Border LegacyFold()
    {
        var panel = new StackPanel();
        _legacySummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        _legacySummary.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        panel.Children.Add(_legacySummary);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var convert = _legacyConvert = new Button { Content = "Convert", Padding = new Thickness(10, 3, 10, 3), ToolTip = "Move it into Sequence Output cards" };
        AutomationProperties.SetName(convert, "Convert Legacy Grade");
        convert.Click += (_, _) => ConvertLegacy();
        var reset = new Button { Content = "Reset", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Reset the legacy sliders and LUT" };
        AutomationProperties.SetName(reset, "Reset Legacy Grade");
        reset.Click += (_, _) => ResetLegacy();
        buttons.Children.Add(convert);
        buttons.Children.Add(reset);
        panel.Children.Add(buttons);
        _legacyCard = InspectorCard("Legacy Grade", panel);
        _legacyCard.Visibility = Visibility.Collapsed;
        return _legacyCard;
    }

    /// <summary>
    /// Shows the Legacy Grade card while the project has a legacy grade (non-neutral sliders or a LUT path) and lists its parts. A legacy LUT
    /// blocked from Convert by an existing Sequence Output LUT (never two per stack) is labelled kept; Convert is disabled when nothing else
    /// remains (Reset still removes the kept LUT).
    /// </summary>
    private void UpdateLegacy()
    {
        if (_legacyCard is null || _legacySummary is null || _vm is not { } vm)
            return;

        var shown = vm.HasLegacyGrade;
        _legacyCard.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown)
            return;

        var grade = vm.GetSequenceGrade();
        var parts = new List<string>();
        if (grade.Balance is not null)
            parts.Add("Brightness & Contrast");
        if (grade.Hue is not null)
            parts.Add("Hue");
        if (grade.Sharpen is not null)
            parts.Add("Sharpen");
        var lutKept = false;
        if (!string.IsNullOrWhiteSpace(vm.LutPath))
        {
            lutKept = vm.SequenceOutputHoldsLut;
            parts.Add("LUT " + Path.GetFileName(vm.LutPath.Trim().Trim('"')) + (lutKept ? " (kept as is)" : ""));
        }

        _legacySummary.Text = string.Join(" · ", parts);
        if (_legacyConvert is not null)
            _legacyConvert.IsEnabled = !grade.IsNeutral || !lutKept;
    }

    // Convert: one Undo step that includes the LUT path (MainViewModel.SequenceLook); no local state.
    private void ConvertLegacy()
    {
        FlushWrites();
        if (_vm is not { } vm)
            return;
        vm.ConvertLegacyGrade();
        UpdateLegacy();
    }

    // Reset: clears the sliders and the legacy LUT (kept or not) as one Undo step (MainViewModel.ResetLegacyGrade), so a LUT that
    // Convert kept can still be removed.
    private void ResetLegacy()
    {
        FlushWrites();
        if (_vm is not { } vm)
            return;
        vm.ResetLegacyGrade();
        UpdateLegacy();
    }

    // ----- Post-grade YRGB histogram (with the pre-grade input as its ghost) -----

    // Timeline or preview changed, so the input may have; ignored within 400 ms of this pane's own write (which changes only the grade).
    private void Invalidated()
    {
        if (Environment.TickCount64 - _lastOwnWrite > 400)
            Want();
    }

    /// <summary>Schedules a throttled input capture, only while the tab is visible and the player paused.</summary>
    private void Want(bool afterRest = false)
    {
        if (!_built || !_listening || !IsVisible || _vm is null || _capture is null)
            return;

        // Nothing to grade (no clip, or an audio clip): curves are hidden, so skip the capture.
        if (!_vm.CanColorGrade(_target))
        {
            _throttle?.Stop();
            return;
        }

        if (_vm.IsPlaying)
        {
            _throttle?.Stop();
            return;
        }

        _wanted = true;
        var throttle = _throttle ??= MakeThrottle();
        if (afterRest)
            throttle.Stop();
        if (!throttle.IsEnabled && !_capturing)
            throttle.Start();
    }

    private DispatcherTimer MakeThrottle()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ThrottleMs) };
        timer.Tick += (_, _) => _ = CaptureAsync();
        return timer;
    }

    private async Task CaptureAsync()
    {
        _throttle?.Stop();
        if (_capturing || !_listening || !IsVisible || _vm is not { IsPlaying: false } || _capture is not { } capture)
            return;

        // During a gesture the pre-grade input stays held (the ghost) and only the displayed, post-grade picture is read, throttled, so the
        // histogram follows the grade live; the full capture runs again when the gesture ends.
        if (InGesture)
        {
            await CapturePostAsync(capture);
            return;
        }

        _capturing = true;
        _wanted = false;
        CaptureCount++;
        try
        {
            // The input with the card excluded (the curves' input and the ghost), then the picture as displayed (post-grade); with no card
            // yet the two are the same frame. Both must be one frame of one target: playback, a gesture, the playhead, the target or its
            // card changing meanwhile drops the result (and the second readback) and asks again.
            var cardId = _card?.Id ?? Guid.Empty;
            var (target, position) = (_target, _vm?.PositionMs);
            bool Current() => _listening && IsVisible && !InGesture && _vm is { IsPlaying: false } now && now.PositionMs == position && _target == target
                && (_card?.Id ?? Guid.Empty) == cardId;

            var frame = await capture(cardId);
            var shown = cardId != Guid.Empty && frame is { Width: > 0, Height: > 0 } && Current() ? await capture(Guid.Empty) : null;
            if (frame is { Width: > 0, Height: > 0 } still && Current())
            {
                var (pre, post) = await Task.Run(() =>
                {
                    var input = YrgbHistogram.Of(still.Bgra, still.Width, still.Height);
                    return (input, cardId == Guid.Empty ? input : shown is { Width: > 0, Height: > 0 } graded ? YrgbHistogram.Of(graded.Bgra, graded.Width, graded.Height) : null);
                });
                if (Current())
                {
                    // No post-grade picture (its readback slot busy): the input counts are the ghost only, never drawn as the post-grade
                    // result, and the capture is asked again.
                    _still = new StampedStill(still.Bgra, still.Width, still.Height, target, cardId, position ?? 0);
                    _curves.SetHistograms(post ?? _curves.Surface.Histogram, pre);
                    GradeGhost.Set(pre, _curves.GradeGesture);
                    _wanted |= post is null;
                }
                else
                {
                    _wanted = true;
                }
            }
            else if (frame is { Width: > 0, Height: > 0 })
            {
                _wanted = true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or TaskCanceledException)
        {
            AppLog.Write("Color Studio: the frame could not be read", ex);
        }
        finally
        {
            _capturing = false;
        }

        if (_wanted && _listening && IsVisible && _vm is { IsPlaying: false })
            _throttle?.Start();
    }

    // One throttled post-grade readback during a gesture: its counts drawn over the held pre-grade ghost (same target, card, frame and
    // gesture, or dropped); the full capture is owed once the gesture ends.
    private async Task CapturePostAsync(Func<Guid, Task<(byte[] Bgra, int Width, int Height)?>> capture)
    {
        if (_curves.Surface.Ghost is not { } held)
            return;
        var (target, position, cardId) = (_target, _vm?.PositionMs, _card?.Id ?? Guid.Empty);
        bool Current() => InGesture && _listening && IsVisible && _vm is { IsPlaying: false } now && now.PositionMs == position && _target == target
            && (_card?.Id ?? Guid.Empty) == cardId && ReferenceEquals(_curves.Surface.Ghost, held);

        _capturing = true;
        (_postWanted, _wanted) = (false, true);
        try
        {
            if (await capture(Guid.Empty) is { Width: > 0, Height: > 0 } shown && Current())
            {
                var post = await Task.Run(() => YrgbHistogram.Of(shown.Bgra, shown.Width, shown.Height));
                if (Current())
                    _curves.SetHistograms(post, held);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or TaskCanceledException)
        {
            AppLog.Write("Color Studio: the frame could not be read", ex);
        }
        finally
        {
            _capturing = false;
        }

        // Written again meanwhile: another post-grade read; the gesture over: the full capture it owes.
        if ((InGesture ? _postWanted : _wanted) && _listening && IsVisible && _vm is { IsPlaying: false })
            _throttle?.Start();
    }

    // A write of this pane's during a gesture: a throttled post-grade read (the pre-grade ghost held) and a full capture owed after it.
    private void WantPost()
    {
        if (!_built || !_listening || !IsVisible || _vm is not { IsPlaying: false } || _capture is null)
            return;
        (_postWanted, _wanted) = (true, true);
        var throttle = _throttle ??= MakeThrottle();
        if (!throttle.IsEnabled && !_capturing)
            throttle.Start();
    }

    // A grading gesture (wheel, Basics, curve, Match Strength) shows the pre-grade ghost on the curves and in Video Scopes until it ends.
    private void ShowGradeGhost(bool active)
    {
        if (!_built)
            return;
        _curves.GradeGesture = active;
        GradeGhost.Set(_curves.Surface.Ghost, active);
    }

    /// <summary>256-bin BT.709 luma histogram of a still (VideoScopes.Histogram's luma row).</summary>
    internal static int[] LumaOf(byte[] bgra, int width, int height)
    {
        var counts = VideoScopes.Histogram(bgra, width, height);
        var luma = new int[256];
        for (var i = 0; i < 256; i++)
            luma[i] = counts[3, i];
        return luma;
    }
}
