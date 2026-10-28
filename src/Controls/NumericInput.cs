using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    // Every slider and every number box in the application is put back by a double-click, with nothing to be
    // set on the control for it. What it goes back to is looked for in three places: the style preset in use
    // (when the settings say so, and it has a value for what the control is bound to), then the Default set
    // on the control, then what a new object of the kind it is bound to starts out with.

    /// <summary>Asks for the style preset's value for a property of an object; set by the main window. Null when there is none.</summary>
    public static Func<object?, string?, double?>? ResetValue { get; set; }

    private static bool _resetRegistered;
    private static readonly Dictionary<Type, object?> FreshObjects = [];

    /// <summary>Starts listening for double-clicks on sliders and number boxes, application-wide. Called once, at start-up.</summary>
    public static void RegisterDoubleClickReset()
    {
        if (_resetRegistered)
            return;

        _resetRegistered = true;
        EventManager.RegisterClassHandler(typeof(Slider), Control.PreviewMouseDoubleClickEvent, new MouseButtonEventHandler(Slider_DoubleClick));
        EventManager.RegisterClassHandler(typeof(TextBox), Control.PreviewMouseDoubleClickEvent, new MouseButtonEventHandler(TextBox_DoubleClick));
    }

    private static void OnDefaultChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is TextBox box)
            HookTextBox(box);
    }

    /// <summary>The value a control goes back to, or null when nothing says what that is.</summary>
    private static double? FindReset(FrameworkElement control, DependencyProperty bound)
    {
        var binding = System.Windows.Data.BindingOperations.GetBindingExpression(control, bound);
        var (source, property) = (binding?.ResolvedSource, binding?.ResolvedSourcePropertyName);
        if (ResetValue?.Invoke(source, property) is { } styled)
            return styled;
        if (double.TryParse(GetDefault(control), NumberStyles.Float, CultureInfo.InvariantCulture, out var given))
            return given;
        if (source is null || property is null)
            return null;

        // What a new one of these starts with. Only for kinds that can simply be made; made once, and kept.
        var type = source.GetType();
        if (!FreshObjects.TryGetValue(type, out var fresh))
        {
            try
            {
                fresh = type.Namespace == "HandPegApp.Models" && type.GetConstructor(Type.EmptyTypes) is not null ? Activator.CreateInstance(type) : null;
            }
            catch (Exception ex) when (ex is MissingMethodException or System.Reflection.TargetInvocationException)
            {
                fresh = null;
            }

            FreshObjects[type] = fresh;
        }

        return fresh is null ? null : type.GetProperty(property)?.GetValue(fresh) switch
        {
            double number => number,
            int number => number,
            _ => null,
        };
    }

    private static void Slider_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Not the timeline: a double-click there is two clicks on a moment, not a wish to go back to the start.
        if (sender is not Slider slider || e.ChangedButton != MouseButton.Left || slider.Name == "TimelineSlider" || FindReset(slider, RangeBase.ValueProperty) is not { } value)
            return;

        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        e.Handled = true;
    }

    private static void TextBox_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only the boxes that hold a number (the ones that are dragged to adjust); in any other a double-click selects a word.
        if (sender is not TextBox box || e.ChangedButton != MouseButton.Left || !(bool)box.GetValue(IsHookedProperty) || FindReset(box, TextBox.TextProperty) is not { } value)
            return;

        var decimals = (int)box.GetValue(DecimalsProperty);
        EndDrag(box);
        SetText(box, value.ToString(decimals > 0 ? "0." + new string('#', decimals) : "0", CultureInfo.InvariantCulture));
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
