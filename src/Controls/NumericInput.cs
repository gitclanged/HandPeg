using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// Attached NLE input behaviours for standard controls, set from XAML:
///
///   Default    value restored by double-clicking a slider or number box;
///   DragStep   value change per pixel when a number box is dragged horizontally.
///
/// An unfocused number box acts as a drag handle: press and move to adjust (Shift = 0.1x, Ctrl = 10x), or click
/// without moving to start typing. Once focused it behaves as a normal text box, including mouse selection.
/// </summary>
public static class NumericInput
{
    // Pointer travel (DIPs) below which a press counts as a click.
    private const double DragThreshold = 3;

    public static readonly DependencyProperty DefaultProperty = DependencyProperty.RegisterAttached(
        "Default", typeof(string), typeof(NumericInput), new PropertyMetadata(null, OnDefaultChanged));

    public static readonly DependencyProperty DragStepProperty = DependencyProperty.RegisterAttached(
        "DragStep", typeof(double), typeof(NumericInput), new PropertyMetadata(0.0, OnDragStepChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.RegisterAttached(
        "Minimum", typeof(double), typeof(NumericInput), new PropertyMetadata(double.NegativeInfinity));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.RegisterAttached(
        "Maximum", typeof(double), typeof(NumericInput), new PropertyMetadata(double.PositiveInfinity));

    /// <summary>Decimal places used to round and format dragged values.</summary>
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.RegisterAttached(
        "Decimals", typeof(int), typeof(NumericInput), new PropertyMetadata(0));

    private static readonly DependencyProperty DragStateProperty = DependencyProperty.RegisterAttached(
        "DragState", typeof(DragState), typeof(NumericInput));

    private static readonly DependencyProperty IsHookedProperty = DependencyProperty.RegisterAttached(
        "IsHooked", typeof(bool), typeof(NumericInput), new PropertyMetadata(false));

    private static readonly DependencyProperty HeldProperty = DependencyProperty.RegisterAttached(
        "Held", typeof(Action<bool>), typeof(NumericInput));

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

    // Application-wide: every slider and hooked number box resets on double-click with no per-control setup. Reset value
    // resolution order: the active style preset (when enabled in settings and it covers the bound property), the control's
    // Default, then the bound property's value on a freshly constructed model object.

    /// <summary>Style preset lookup for (source, property), installed by the main window; returns null when not covered.</summary>
    public static Func<object?, string?, double?>? ResetValue { get; set; }

    private static bool _resetRegistered;
    private static readonly Dictionary<Type, object?> FreshObjects = [];

    /// <summary>Registers application-wide double-click handlers for sliders and text boxes. Idempotent; called at start-up.</summary>
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

    /// <summary>Resolves a control's reset value, or null when no source defines one.</summary>
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

        // Fresh-instance default: only for parameterless HandPegApp.Models types; one cached instance per type.
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
        // Skip TimelineSlider: a double-click there is two seeks, not a reset.
        if (sender is not Slider slider || e.ChangedButton != MouseButton.Left || slider.Name == "TimelineSlider" || FindReset(slider, RangeBase.ValueProperty) is not { } value)
            return;

        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        e.Handled = true;
    }

    private static void TextBox_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only hooked number boxes; in other text boxes a double-click keeps its word-select behaviour.
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

    /// <summary>Hooks Enter to push typed text to the source (for LostFocus-bound boxes) and marks the box as a number box.</summary>
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

    // SizeWE cursor signals draggable; IBeam while focused for typing.
    private static void UpdateCursor(TextBox box) => box.Cursor = box.IsKeyboardFocusWithin ? Cursors.IBeam : Cursors.SizeWE;

    private static void TextBox_FocusChanged(object sender, KeyboardFocusChangedEventArgs e) => UpdateCursor((TextBox)sender);

    private static void TextBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;

        // Focused: normal caret placement and selection. Non-numeric text (e.g. a blank size) has nothing to drag, so the
        // click just starts typing.
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

        if (!state.IsDragging)
            TellHeld(box, true);
        state.IsDragging = true;

        // Subtract the threshold travel so the value starts from its origin instead of jumping.
        distance -= Math.Sign(distance) * Math.Min(DragThreshold, Math.Abs(distance));

        var speed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1
            : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10
            : 1;

        // Absolute from the drag origin (not incremental): the value tracks the pointer exactly, returns when the pointer does, and
        // accumulates no rounding error.
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

        // Click without drag: focus and select all for typing.
        if (!state.IsDragging)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private static void TextBox_LostMouseCapture(object sender, MouseEventArgs e)
    {
        var box = (TextBox)sender;
        LetGo(box);
        box.ClearValue(DragStateProperty);
    }

    private static void EndDrag(TextBox box)
    {
        LetGo(box);
        box.ClearValue(DragStateProperty);
        if (box.IsMouseCaptured)
            box.ReleaseMouseCapture();
    }

    // Reports release for an active drag (once: callers clear the state immediately after).
    private static void LetGo(TextBox box)
    {
        if (box.GetValue(DragStateProperty) is DragState { IsDragging: true })
            TellHeld(box, false);
    }

    // ----- Pointer hold tracking -----

    /// <summary>
    /// Raised when any number box starts (true) or stops (false) a pointer drag, in any window; drives hold-only UI such as the player's
    /// edge loupe. Typing, or a click that starts typing, is not a hold.
    /// </summary>
    public static event Action<TextBox, bool>? DragHeld;

    /// <summary>
    /// Reports pointer holds on a slider (any press) or number box (drag past the threshold): true on start, false on release or
    /// capture loss. Keyboard nudges are not holds.
    /// </summary>
    public static void WatchHold(Control control, Action<bool> held)
    {
        if (control is TextBox)
        {
            control.SetValue(HeldProperty, held);
            return;
        }

        control.PreviewMouseLeftButtonDown += (_, _) => held(true);
        control.PreviewMouseLeftButtonUp += (_, _) => held(false);
        control.LostMouseCapture += (_, _) => held(false);
    }

    // ----- Clip edge tuning -----

    /// <summary>
    /// Clip edge a slider or number box shapes (feather, mask, chroma key), set in XAML. Setting it hooks <see cref="WatchHold"/>: while
    /// held, <see cref="EdgeHeld"/> lets the window magnify that edge of the DataContext clip. None (default) hooks nothing.
    /// </summary>
    public static readonly DependencyProperty EdgeKindProperty = DependencyProperty.RegisterAttached(
        "EdgeKind", typeof(EdgeTuningKind), typeof(NumericInput), new PropertyMetadata(EdgeTuningKind.None, OnEdgeKindChanged));

    public static EdgeTuningKind GetEdgeKind(DependencyObject element) => (EdgeTuningKind)element.GetValue(EdgeKindProperty);
    public static void SetEdgeKind(DependencyObject element, EdgeTuningKind value) => element.SetValue(EdgeKindProperty, value);

    /// <summary>Raised when a control with an <see cref="EdgeKindProperty"/> starts (true) or stops (false) a pointer hold, in any window.</summary>
    public static event Action<FrameworkElement, EdgeTuningKind, bool>? EdgeHeld;

    // Hooked once on the first non-None kind; later kind changes are read when a hold begins.
    private static void OnEdgeKindChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is Control control && e.OldValue is EdgeTuningKind.None && e.NewValue is not EdgeTuningKind.None)
            WatchHold(control, held => EdgeHeld?.Invoke(control, GetEdgeKind(control), held));
    }

    private static void TellHeld(TextBox box, bool held)
    {
        (box.GetValue(HeldProperty) as Action<bool>)?.Invoke(held);
        DragHeld?.Invoke(box, held);
    }

    /// <summary>Sets the box text and pushes it to the binding source immediately.</summary>
    private static void SetText(TextBox box, string text)
    {
        box.Text = text;
        BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateSource();
    }
}
