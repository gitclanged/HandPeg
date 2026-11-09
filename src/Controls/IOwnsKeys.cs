using System.Windows.Input;

namespace HandPegApp;

/// <summary>
/// A control that keeps some keys for itself while it (or anything inside it) has the keyboard focus (5.4C): the main window's
/// shortcuts then leave those keys to it (MainWindow.KeyBelongsToFocus). The Media Browser's filmstrip cutter keeps I, O, Space and
/// the arrows; the Subtitle Studio keeps Delete for its cues.
/// </summary>
public interface IOwnsKeys
{
    /// <summary>Whether <paramref name="key"/> with <paramref name="modifiers"/> is this control's own while it has the focus.</summary>
    bool OwnsKey(Key key, ModifierKeys modifiers);
}
