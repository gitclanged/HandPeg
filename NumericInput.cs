using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace HandPegApp;

/// <summary>
/// Two habits of editing software, added to ordinary controls from XAML:
///
///   Default    double-clicking a slider or a number box puts it back to this value;
///   DragStep   a number box can be dragged sideways to change its value, this much per pixel.
///
/// A number box that is not being typed in is a drag handle: press and move to adjust (Shift for a tenth
/// of the speed, Ctrl for ten times), or click without moving to start typing. Once it has the keyboard
/// it behaves like any text box, so text can still be selected with the mouse.
/// </summary>
public static class NumericInput
{
    // Movement below this is a click, not a drag.
    private const double DragThreshold = 3;

    public static readonly DependencyProperty DefaultProperty = DependencyProperty.RegisterAttached(
        "Default", typeof(string), typeof(NumericInput), new PropertyMetadata(null, OnDefaultChanged));

    public static readonly DependencyProperty DragStepProperty = DependencyProperty.RegisterAttached(
        "DragStep", typeof(double), typeof(NumericInput), new PropertyMetadata(0.0, OnDragStepChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.RegisterAttached(
        "Minimum", typeof(double), typeof(NumericInput), new PropertyMetadata(double.NegativeInfinity));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.RegisterAttached(
        "Maximum", typeof(double), typeof(NumericInput), new PropertyMetadata(double.PositiveInfinity));

    /// <summary>Digits after the decimal point that a dragged value is rounded to and written with.</summary>
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.RegisterAttached(
        "Decimals", typeof(int), typeof(NumericInput), new PropertyMetadata(0));

    private static readonly DependencyProperty DragStateProperty = DependencyProperty.RegisterAttached(
        "DragState", typeof(DragState), typeof(NumericInput));

    private static readonly DependencyProperty IsHookedProperty = DependencyProperty.RegisterAttached(
        "IsHooked", typeof(bool), typeof(NumericInput), new PropertyMetadata(false));

    public static string? GetDefault(DependencyObject element) => (string?)element.GetValue(DefaultProperty);
    public static void SetDefault(DependencyObject element, string? value) => element.SetValue(DefaultProperty, value);
    public static double GetDragStep(DependencyObject element) => (double)element.GetValue(DragStepProperty);
    public static void SetDragStep(DependencyObject element, double value) => element.SetValue(DragStepProperty, value);
    public static double GetMinimum(DependencyObject element) => (double)element.GetValue(MinimumProperty);
    public static void SetMinimum(DependencyObject element, double value) => element.SetValue(MinimumProperty, value);
    public static double GetMaximum(DependencyObject element) => (double)element.GetValue(MaximumProperty);
    public static void SetMaximum(DependencyObject element, double value) => element.SetValue(MaximumProperty, value);
    public static int GetDecimals(DependencyObject element) => (int)element.GetValue(DecimalsProperty);
    public static void SetDecimals(DependencyObject element, int value) => element.SetValue(DecimalsProperty, value);

    private sealed class DragState(double startX, double startValue)
    {
        public double StartX { get; } = startX;
        public double StartValue { get; } = startValue;
        public bool IsDragging { get; set; }
    }

    // ----- Double-click reset -----

    private static void OnDefaultChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        switch (element)
        {
            case Slider slider:
                slider.PreviewMouseDoubleClick -= Slider_DoubleClick;
                if (e.NewValue is not null)
                    slider.PreviewMouseDoubleClick += Slider_DoubleClick;
                break;

            case TextBox box:
                box.PreviewMouseDoubleClick -= TextBox_DoubleClick;
                if (e.NewValue is not null)
                    box.PreviewMouseDoubleClick += TextBox_DoubleClick;
                HookTextBox(box);
                break;
        }
    }

    private static void Slider_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (e.ChangedButton != MouseButton.Left
            || !double.TryParse(GetDefault(slider), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return;
        }

        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        e.Handled = true;
    }

    private static void TextBox_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.ChangedButton != MouseButton.Left || GetDefault(box) is not { } value)
            return;

        EndDrag(box);
        SetText(box, value);
        e.Handled = true;
    }

    // ----- Drag to adjust -----

    private static void OnDragStepChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBox box)
            return;

        HookTextBox(box);
        box.PreviewMouseLeftButtonDown -= TextBox_MouseDown;
        box.PreviewMouseMove -= TextBox_MouseMove;
        box.PreviewMouseLeftButtonUp -= TextBox_MouseUp;
        box.LostMouseCapture -= TextBox_LostMouseCapture;
        box.GotKeyboardFocus -= TextBox_FocusChanged;
        box.LostKeyboardFocus -= TextBox_FocusChanged;

        if ((double)e.NewValue > 0)
        {
            box.PreviewMouseLeftButtonDown += TextBox_MouseDown;
            box.PreviewMouseMove += TextBox_MouseMove;
            box.PreviewMouseLeftButtonUp += TextBox_MouseUp;
            box.LostMouseCapture += TextBox_LostMouseCapture;
            box.GotKeyboardFocus += TextBox_FocusChanged;
            box.LostKeyboardFocus += TextBox_FocusChanged;
            UpdateCursor(box);
        }
        else
        {
            box.ClearValue(FrameworkElement.CursorProperty);
        }
    }

    /// <summary>Enter commits what was typed, for boxes whose value is otherwise only taken when they lose the keyboard.</summary>
    private static void HookTextBox(TextBox box)
    {
        if ((bool)box.GetValue(IsHookedProperty))
            return;

        box.SetValue(IsHookedProperty, true);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateSource();
                box.SelectAll();
            }
        };
    }

    // The sideways arrow says "drag me"; the text cursor comes back while the box is being typed in.
    private static void UpdateCursor(TextBox box) => box.Cursor = box.IsKeyboardFocusWithin ? Cursors.IBeam : Cursors.SizeWE;

    private static void TextBox_FocusChanged(object sender, KeyboardFocusChangedEventArgs e) => UpdateCursor((TextBox)sender);

    private static void TextBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;

        // Being typed in: the mouse places the caret and selects text as usual. A box without a number in
        // it (a blank size, say) has nothing to adjust, so a click there simply starts typing too.
        if (box.IsKeyboardFocusWithin || !box.IsEnabled
            || !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return;
        }

        box.SetValue(DragStateProperty, new DragState(e.GetPosition(box).X, value));
        box.CaptureMouse();
        e.Handled = true;
    }

    private static void TextBox_MouseMove(object sender, MouseEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.GetValue(DragStateProperty) is not DragState state || !box.IsMouseCaptured)
            return;

        var distance = e.GetPosition(box).X - state.StartX;
        if (!state.IsDragging && Math.Abs(distance) < DragThreshold)
            return;

        state.IsDragging = true;

        // The few pixels it took to recognise the drag are not counted, so the value starts moving from
        // where it was instead of jumping by that much.
        distance -= Math.Sign(distance) * Math.Min(DragThreshold, Math.Abs(distance));

        var speed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1
            : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10
            : 1;

        // Always worked out from where the drag began, never added to step by step: the value then follows
        // the pointer exactly, comes back to where it was when the pointer does, and loses nothing to rounding.
        var decimals = Math.Clamp(GetDecimals(box), 0, 6);
        var value = Math.Round(state.StartValue + distance * GetDragStep(box) * speed, decimals, MidpointRounding.AwayFromZero);
        value = Math.Clamp(value, GetMinimum(box), GetMaximum(box));
        SetText(box, value.ToString("F" + decimals, CultureInfo.InvariantCulture));
    }

    private static void TextBox_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.GetValue(DragStateProperty) is not DragState state)
            return;

        EndDrag(box);
        e.Handled = true;

        // Pressed and released in place: that was a click, and a click means "let me type".
        if (!state.IsDragging)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private static void TextBox_LostMouseCapture(object sender, MouseEventArgs e) => ((TextBox)sender).ClearValue(DragStateProperty);

    private static void EndDrag(TextBox box)
    {
        box.ClearValue(DragStateProperty);
        if (box.IsMouseCaptured)
            box.ReleaseMouseCapture();
    }

    /// <summary>Writes a value into the box and on to whatever it is bound to, whenever that binding normally updates.</summary>
    private static void SetText(TextBox box, string text)
    {
        box.Text = text;
        BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateSource();
    }
}
