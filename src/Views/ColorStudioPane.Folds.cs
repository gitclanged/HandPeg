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

// ----- Pass 5.6A: the Color Studio's LUT, Output and Legacy Grade folds, and its histogram before the grade -----
public partial class ColorStudioPane
{
    /// <summary>The least time between two captures of the input still, milliseconds (about 8 Hz, as the Video Scopes).</summary>
    internal const int ThrottleMs = 125;

    // A trailing throttle (made on first use): a change starts it, its tick captures; changes meanwhile are one capture.
    private DispatcherTimer? _throttle;
    private bool _capturing, _wanted;
    private Border? _legacyCard;
    private TextBlock? _legacySummary;
    private Button? _legacyConvert;

    // The LUT box's text as the pane last put it (5.6A): leaving the box commits only a path the user typed.
    private string _lutShown = "";

    /// <summary>How many input stills were asked for (the goldens: none while hidden or playing).</summary>
    internal int CaptureCount { get; private set; }

    // ----- The LUT -----

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

    // Only a path the user typed (5.6A): leaving the box untouched writes nothing (an Undo's path stays undone).
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

    // ----- Output: the whole picture's switches (their one home since the Filters tab went) -----

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

    // ----- Legacy Grade: the old Filters tab's sliders and LUT, while the project has them -----

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
    /// The Legacy Grade card shown while the project has the old grade (sliders not neutral, or a LUT path), naming what it holds. A LUT a
    /// Sequence Output card's LUT keeps out of Convert (5.6A: never two on one stack) is said to be kept; Convert is off when nothing else is left
    /// (Reset still takes the kept LUT away).
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

    // Convert (5.6A): one step of Undo that holds the LUT path too (MainViewModel.SequenceLook), so nothing is kept here.
    private void ConvertLegacy()
    {
        FlushWrites();
        if (_vm is not { } vm)
            return;
        vm.ConvertLegacyGrade();
        UpdateLegacy();
    }

    // Reset (5.6A): the sliders and the legacy LUT, kept or not, as one step of Undo (MainViewModel.ResetLegacyGrade), so a LUT Convert
    // keeps as it is can still be taken away.
    private void ResetLegacy()
    {
        FlushWrites();
        if (_vm is not { } vm)
            return;
        vm.ResetLegacyGrade();
        UpdateLegacy();
    }

    // ----- The histogram before the grade -----

    // The timeline or the preview changed: the input may have (a write of this pane's own changes only the grade: not captured for).
    private void Invalidated()
    {
        if (Environment.TickCount64 - _lastOwnWrite > 400)
            Want();
    }

    /// <summary>The input may have changed: captured once the throttle lets it, only while the tab is in sight and the player paused.</summary>
    private void Want(bool afterRest = false)
    {
        if (!_built || !_listening || !IsVisible || _vm is null || _capture is null)
            return;

        // Nothing to grade (5.6A: no clip, a sound clip): the curves are hidden, so no still is asked for.
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

        // Not in the middle of a drag: the still would wait behind its frames (it is asked for as the drag ends).
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

    /// <summary>A still's BT.709 luma counted at each of 256 levels (VideoScopes.Histogram's luma row).</summary>
    internal static int[] LumaOf(byte[] bgra, int width, int height)
    {
        var counts = VideoScopes.Histogram(bgra, width, height);
        var luma = new int[256];
        for (var i = 0; i < 256; i++)
            luma[i] = counts[3, i];
        return luma;
    }
}
