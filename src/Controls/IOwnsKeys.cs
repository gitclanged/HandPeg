using System.Windows.Input;

namespace HandPegApp;

/// <summary>
/// Control that claims certain keys while it (or a descendant) has keyboard focus; main-window shortcuts then yield them
/// (MainWindow.KeyBelongsToFocus). E.g. the filmstrip cutter claims I, O, Space and arrows; the Subtitle Studio and Project Media
/// list claim Delete.
/// </summary>
public interface IOwnsKeys
{
    /// <summary>True when this control claims <paramref name="key"/> with <paramref name="modifiers"/> while focused.</summary>
    bool OwnsKey(Key key, ModifierKeys modifiers);
}
