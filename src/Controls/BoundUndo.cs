using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// A dialog's own Undo, over what its controls are bound to (4.3E; the Layer Tool's first, shared since). Each press of the
/// pointer or a key that changes something (a slider dragged, a value typed and handed on, a box ticked, a button) is a step:
/// what the shown controls are bound to is read as the press begins, and kept as a step when the press has changed it.
/// Ctrl+Z puts the last step back; Ctrl+Y or Ctrl+Shift+Z goes forward again. A text box keeps the keys for its own typing
/// until it has handed that on (Enter, or leaving the box). For a modal dialog: the main window's Undo can not be reached
/// while it is open, so the two never fight, and the main window takes back the whole visit once it is closed.
/// </summary>
public sealed class BoundUndo
{
    private const int Depth = 50;

    private static readonly DependencyProperty[] BoundProperties =
        [TextBox.TextProperty, RangeBase.ValueProperty, ToggleButton.IsCheckedProperty, Selector.SelectedValueProperty, Selector.SelectedItemProperty];

    // A value kept: where it lives, and what it was.
    private readonly record struct Kept(object Source, PropertyInfo Property, object? Value);

    private readonly Window _window;
    private readonly Func<IEnumerable<(object? Source, string Property)>>? _alsoKept;
    private readonly List<Kept[]> _undo = [], _redo = [];

    // What the shown controls are bound to now, and their bindings (shown again after a step is put back: a model that does not
    // say when it changes is not followed by its controls).
    private (object Source, PropertyInfo Property)[] _bound = [];
    private BindingExpression[] _bindings = [];

    // What the kept values were as the press now going on began; null between presses.
    private Kept[]? _beforePress;

    /// <param name="alsoKept">Values the controls do not show but buttons set (the drawn mask's shapes), kept with the rest.</param>
    public BoundUndo(Window window, Func<IEnumerable<(object? Source, string Property)>>? alsoKept = null)
    {
        (_window, _alsoKept) = (window, alsoKept);
        window.Loaded += (_, _) => Find();
        window.PreviewMouseDown += (_, _) => BeginPress();
        window.PreviewKeyDown += Window_PreviewKeyDown;
        window.AddHandler(UIElement.MouseUpEvent, new MouseButtonEventHandler((_, _) => EndPress()), handledEventsToo: true);
        window.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, _) => EndPress()), handledEventsToo: true);
    }

    /// <summary>Raised when there is newly something (or nothing) to undo or redo: for the dialog's buttons.</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public void Undo() => Step(_undo, _redo);

    public void Redo() => Step(_redo, _undo);

    /// <summary>
    /// After a change that reaches beyond what the dialog shows (a mask given to every layer, layers made from it): its steps,
    /// which know only what it shows, are let go. The main window's Undo takes it all back once the dialog is closed.
    /// </summary>
    public void Forget()
    {
        _undo.Clear();
        _redo.Clear();
        _beforePress = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// What the shown controls are bound to: the values kept. Looked for again as every press begins, so controls shown or
    /// bound afresh since are kept too; each step holds where its values live, so older steps are still put back right.
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
            // A hidden panel's controls are not the dialog's now: another tool's, or the main video's place for another layer.
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

    // The values a step holds, as they are now.
    private static Kept[] Now(Kept[] step) => Read(step.Select(k => (k.Source, k.Property)));

    private static bool Same(Kept[] step) => step.All(k => Equals(k.Property.GetValue(k.Source), k.Value));

    private void BeginPress()
    {
        if (_beforePress is not null)
            return;

        Find();
        _beforePress = Read(_bound);
    }

    // A press has ended (the pointer let go, a key let up): a step, when it changed something.
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

        // A text box's own typing, not yet handed on, is taken back (or put back) in the box first.
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

    /// <summary>One step back (or forward again): the values as the step held them, and as they are now kept to come back to.</summary>
    private void Step(List<Kept[]> from, List<Kept[]> to)
    {
        // The press that asked for it (a click of a button, Ctrl held for the key) is not a change of its own.
        _beforePress = null;

        // A step that would change nothing (a text box's typing already taken back in the box) is passed over.
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
        // Twice over: setting one value can move another with it (a mask chosen ticks its switch), which the second pass puts back.
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

        // The controls show the values put back, also where the model does not say it changed (a caption style, audio filters).
        foreach (var binding in _bindings)
        {
            if (binding.Status == BindingStatus.Active)
                binding.UpdateTarget();
        }
    }
}
