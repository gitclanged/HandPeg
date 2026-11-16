using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace HandPegApp;

/// <summary>
/// A dialog's own Undo over one typed state, kept whole: the state is captured as each press of the pointer or a key begins, and kept
/// as a step when the press has changed it (compared by <c>key</c>, a canonical text of the state). Ctrl+Z restores the last step whole;
/// Ctrl+Y or Ctrl+Shift+Z goes forward again. Unlike <see cref="BoundUndo"/>, which reads the properties the shown controls are bound
/// to, it also takes back what no control is bound to (list edits, a whole state replaced by Reset), and a restore never writes into
/// an object the dialog no longer shows. A text box keeps the keys for its own typing until it has handed that on.
/// </summary>
public sealed class SnapshotUndo<T>
    where T : class
{
    private const int Depth = 50;

    private readonly Func<T> _capture;
    private readonly Action<T> _restore;
    private readonly Func<T, string> _key;
    private readonly List<(T State, string Key)> _undo = [], _redo = [];
    private (T State, string Key)? _beforePress;

    /// <param name="window">The dialog whose presses and keys make the steps.</param>
    /// <param name="capture">A deep copy of the state as it is now.</param>
    /// <param name="restore">Puts a captured state back (the dialog shows it, rebinding what it must).</param>
    /// <param name="key">A canonical text of a state: two states are one step apart when their keys differ.</param>
    public SnapshotUndo(Window window, Func<T> capture, Action<T> restore, Func<T, string> key)
    {
        (_capture, _restore, _key) = (capture, restore, key);
        window.PreviewMouseDown += (_, _) => BeginPress();
        window.PreviewKeyDown += Window_PreviewKeyDown;
        window.AddHandler(UIElement.MouseUpEvent, new MouseButtonEventHandler((_, _) => EndPress()), handledEventsToo: true);
        window.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, _) => EndPress()), handledEventsToo: true);
    }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public void Undo() => Step(_undo, _redo);

    public void Redo() => Step(_redo, _undo);

    /// <summary>A press begins: the state as it is now is what a change made by this press is undone to.</summary>
    internal void BeginPress()
    {
        if (_beforePress is not null)
            return;
        var state = _capture();
        _beforePress = (state, _key(state));
    }

    /// <summary>A press has ended: a step, when it changed the state.</summary>
    internal void EndPress()
    {
        if (_beforePress is not { } before)
            return;

        _beforePress = null;
        if (_key(_capture()) == before.Key)
            return;

        _undo.Add(before);
        if (_undo.Count > Depth)
            _undo.RemoveAt(0);
        _redo.Clear();
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

    private void Step(List<(T State, string Key)> from, List<(T State, string Key)> to)
    {
        // The press that asked for it (a click of a button, Ctrl held for the key) is not a change of its own.
        _beforePress = null;
        var now = _capture();
        var nowKey = _key(now);

        // A step that would change nothing is passed over.
        while (from.Count > 0 && from[^1].Key == nowKey)
            from.RemoveAt(from.Count - 1);
        if (from.Count == 0)
            return;

        var (state, _) = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add((now, nowKey));
        if (to.Count > Depth)
            to.RemoveAt(0);
        _restore(state);
    }
}
