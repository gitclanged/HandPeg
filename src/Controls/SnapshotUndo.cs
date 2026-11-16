using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace HandPegApp;

/// <summary>
/// Dialog-local Undo over whole-state snapshots of one typed state: captured at each pointer/key press start and recorded as a step if
/// the press changed it (compared via <c>key</c>, a canonical text form). Ctrl+Z restores the previous snapshot; Ctrl+Y or Ctrl+Shift+Z
/// redoes. Unlike <see cref="BoundUndo"/> (which reads bound control properties), it also reverts unbound changes (list edits, Reset
/// replacing the whole state), and a restore never writes into objects the dialog no longer shows. A text box handles its own
/// uncommitted typing first.
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

    /// <param name="window">Dialog whose pointer and key presses delimit steps.</param>
    /// <param name="capture">Returns a deep copy of the current state.</param>
    /// <param name="restore">Restores a captured state (the dialog displays it, rebinding as needed).</param>
    /// <param name="key">Canonical text of a state; differing keys mean a change.</param>
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

    /// <summary>Press start: snapshots the current state as the undo target for changes made by this press.</summary>
    internal void BeginPress()
    {
        if (_beforePress is not null)
            return;
        var state = _capture();
        _beforePress = (state, _key(state));
    }

    /// <summary>Press end: records a step if the state changed.</summary>
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

    private void Step(List<(T State, string Key)> from, List<(T State, string Key)> to)
    {
        // The triggering press (button click, Ctrl key) must not record its own step.
        _beforePress = null;
        var now = _capture();
        var nowKey = _key(now);

        // Skip no-op steps (same key as the current state).
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
