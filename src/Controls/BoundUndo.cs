using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Dialog-local Undo over the visible controls' binding sources (shared by the Layer Tool and other dialogs). Each pointer or key press
/// that changes something (slider drag, committed text, checkbox, button) becomes a step: bound values are snapshotted at press start and
/// recorded if the press changed them. Ctrl+Z undoes; Ctrl+Y or Ctrl+Shift+Z redoes. A text box handles its own uncommitted typing first
/// (until Enter or blur). Intended for modal dialogs: the main window's Undo is unreachable while open, so the two never conflict, and the
/// main window's Undo reverts the whole dialog session afterwards.
/// </summary>
public sealed class BoundUndo
{
    private const int Depth = 50;

    private static readonly DependencyProperty[] BoundProperties =
        [TextBox.TextProperty, RangeBase.ValueProperty, ToggleButton.IsCheckedProperty, Selector.SelectedValueProperty, Selector.SelectedItemProperty];

    // Snapshot entry: source object, property and value.
    private readonly record struct Kept(object Source, PropertyInfo Property, object? Value);

    private readonly Window _window;
    private readonly Func<IEnumerable<(object? Source, string Property)>>? _alsoKept;
    private readonly List<Kept[]> _undo = [], _redo = [];

    // Current binding sources of visible controls and their expressions (refreshed after a restore, since models without change
    // notification would not update their controls).
    private (object Source, PropertyInfo Property)[] _bound = [];
    private BindingExpression[] _bindings = [];

    // Snapshot taken at the start of the current press; null between presses.
    private Kept[]? _beforePress;

    /// <param name="alsoKept">Extra (source, property) pairs set by buttons but not shown by controls (e.g. drawn mask shapes).</param>
    public BoundUndo(Window window, Func<IEnumerable<(object? Source, string Property)>>? alsoKept = null)
    {
        (_window, _alsoKept) = (window, alsoKept);
        window.Loaded += (_, _) => Find();
        window.PreviewMouseDown += (_, _) => BeginPress();
        window.PreviewKeyDown += Window_PreviewKeyDown;
        window.AddHandler(UIElement.MouseUpEvent, new MouseButtonEventHandler((_, _) => EndPress()), handledEventsToo: true);
        window.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, _) => EndPress()), handledEventsToo: true);
    }

    /// <summary>Raised when undo/redo availability may have changed (drives the dialog's buttons).</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public void Undo() => Step(_undo, _redo);

    public void Redo() => Step(_redo, _undo);

    /// <summary>
    /// Clears local history after a change that reaches beyond the dialog's bindings (e.g. a mask applied to every layer, or layers
    /// created from it). The main window's Undo reverts it after the dialog closes.
    /// </summary>
    public void Forget()
    {
        _undo.Clear();
        _redo.Clear();
        _beforePress = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Collects the visible controls' binding sources. Re-run at every press start so newly shown or rebound controls are included; each
    /// step stores its own sources, so older steps still restore correctly.
    /// </summary>
    private void Find()
    {
        var found = new List<(object Source, PropertyInfo Property)>();
        var bindings = new List<BindingExpression>();
        void Add(object? source, string? name)
        {
            if (source is null || string.IsNullOrEmpty(name))
                return;

            PropertyInfo? property;
            try
            {
                property = source.GetType().GetProperty(name);
            }
            catch (AmbiguousMatchException)
            {
                return;
            }

            if (property is { CanRead: true, CanWrite: true } && property.GetIndexParameters().Length == 0
                && !found.Any(f => ReferenceEquals(f.Source, source) && f.Property == property))
                found.Add((source, property));
        }

        void Walk(object node)
        {
            // Skip hidden panels: their controls belong to another tool or layer context.
            if (node is not DependencyObject element || element is UIElement { Visibility: not Visibility.Visible })
                return;

            foreach (var bound in BoundProperties)
            {
                if (BindingOperations.GetBindingExpression(element, bound) is { } binding)
                {
                    bindings.Add(binding);
                    Add(binding.ResolvedSource, binding.ResolvedSourcePropertyName);
                }
            }

            foreach (var child in LogicalTreeHelper.GetChildren(element))
                Walk(child);
        }

        Walk(_window);
        foreach (var (source, property) in _alsoKept?.Invoke() ?? [])
            Add(source, property);
        (_bound, _bindings) = ([.. found], [.. bindings]);
    }

    private static Kept[] Read(IEnumerable<(object Source, PropertyInfo Property)> where) =>
        [.. where.Select(w => new Kept(w.Source, w.Property, w.Property.GetValue(w.Source)))];

    // Current values for a step's (source, property) pairs.
    private static Kept[] Now(Kept[] step) => Read(step.Select(k => (k.Source, k.Property)));

    private static bool Same(Kept[] step) => step.All(k => Equals(k.Property.GetValue(k.Source), k.Value));

    private void BeginPress()
    {
        if (_beforePress is not null)
            return;

        Find();
        _beforePress = Read(_bound);
    }

    // Press ended (mouse up or key up): record a step if anything changed.
    private void EndPress()
    {
        if (_beforePress is not { } before)
            return;

        _beforePress = null;
        if (Same(before))
            return;

        _undo.Add(before);
        if (_undo.Count > Depth)
            _undo.RemoveAt(0);
        _redo.Clear();
        Changed?.Invoke();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var undo = e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control;
        var redo = (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control) || (e.Key == Key.Z && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
        if (!undo && !redo)
        {
            BeginPress();
            return;
        }

        // Uncommitted text box typing is undone or redone inside the box first.
        if (Keyboard.FocusedElement is TextBox box && (redo ? box.CanRedo : box.CanUndo)
            && BindingOperations.GetBindingExpression(box, TextBox.TextProperty) is not { IsDirty: false })
        {
            if (redo)
                box.Redo();
            else
                box.Undo();
            _beforePress = null;
        }
        else if (redo)
        {
            Redo();
        }
        else
        {
            Undo();
        }

        e.Handled = true;
    }

    /// <summary>Moves one step between stacks: restores the step's values and pushes the current values onto the opposite stack.</summary>
    private void Step(List<Kept[]> from, List<Kept[]> to)
    {
        // The triggering press (button click, Ctrl key) must not record its own step.
        _beforePress = null;

        // Skip no-op steps (e.g. typing already undone inside the text box).
        while (from.Count > 0 && Same(from[^1]))
            from.RemoveAt(from.Count - 1);

        if (from.Count > 0)
        {
            var state = from[^1];
            from.RemoveAt(from.Count - 1);
            to.Add(Now(state));
            if (to.Count > Depth)
                to.RemoveAt(0);
            Put(state);
        }

        Changed?.Invoke();
    }

    private void Put(Kept[] values)
    {
        // Two passes: setting one value can change another (choosing a mask enables its switch); the second pass restores it.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var (source, property, value) in values)
            {
                try
                {
                    if (!Equals(property.GetValue(source), value))
                        property.SetValue(source, value);
                }
                catch (TargetInvocationException ex)
                {
                    AppLog.Write($"Undo in {_window.Title}: {property.Name} could not be put back", ex);
                }
            }
        }

        // Force binding targets to refresh, covering models without change notification (caption style, audio filters).
        foreach (var binding in _bindings)
        {
            if (binding.Status == BindingStatus.Active)
                binding.UpdateTarget();
        }
    }
}
