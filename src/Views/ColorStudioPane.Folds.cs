using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp.Views;

// ----- Color Studio LUT, Output and Legacy Grade sections, and the pre-grade histogram -----
public partial class ColorStudioPane
{
    /// <summary>Minimum interval between input-still captures, ms (~8 Hz, matching Video Scopes).</summary>
    internal const int ThrottleMs = 125;

    // Trailing throttle (created lazily): a change arms it, the tick captures; intermediate changes coalesce.
    private DispatcherTimer? _throttle;
    private bool _capturing, _wanted;
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

    // ----- Output: whole-picture switches (their single home since the Filters tab was removed) -----

    private Expander OutputFold()
    {
        var switches = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        CheckBox Switch(string label, string property, string tip)
        {
            var box = new CheckBox { Content = label, Margin = new Thickness(0, 2, 16, 2), ToolTip = tip };
            box.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(property) { Source = _vm, Mode = BindingMode.TwoWay });
            AutomationProperties.SetName(box, label);
            switches.Children.Add(box);
            return box;
        }

        Switch("Deinterlace", nameof(MainViewModel.Deinterlace), "Draws interlaced video as whole frames");
        Switch("Denoise", nameof(MainViewModel.Denoise), "Smooths grain over the whole picture");
        Switch("Fade In", nameof(MainViewModel.FadeIn), "Picture and sound in from black (1 s)");
        Switch("Fade Out", nameof(MainViewModel.FadeOut), "Picture and sound out to black (1 s)");
        var fold = new Expander { Header = "Output", Content = switches, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(fold, "Output");
        return fold;
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

    // ----- Pre-grade histogram -----

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

        // Skip during a gesture: the readback would queue behind its frames (requested again when the gesture ends).
        if (InGesture)
            return;

        _capturing = true;
        _wanted = false;
        CaptureCount++;
        try
        {
            var frame = await capture(_card?.Id ?? Guid.Empty);
            if (frame is { Width: > 0, Height: > 0 } still)
            {
                var luma = await Task.Run(() => LumaOf(still.Bgra, still.Width, still.Height));
                _curves.SetHistogram(luma);
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
