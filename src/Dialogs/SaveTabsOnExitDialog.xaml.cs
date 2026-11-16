using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>Result of the combined unsaved-tabs dialog.</summary>
public enum TabsExitChoice
{
    /// <summary>Cancel, Esc or title bar close: nothing happens (app or tabs stay open).</summary>
    Cancel,

    /// <summary>Save checked tabs, then close all (unchecked tabs discarded).</summary>
    SaveChecked,

    /// <summary>On exit: suspend all tabs for next launch. On Close Other Tabs: keep unsaved tabs open.</summary>
    KeepSuspended,

    /// <summary>Discard all unsaved tabs.</summary>
    Discard,
}

/// <summary>
/// Dialog row for a project tab: unsaved tabs have a save checkbox (and a name box if never saved); saved tabs (<see cref="Saved"/>)
/// show as saved with no checkbox.
/// </summary>
public sealed partial class TabExitRow : ObservableObject
{
    public TabExitRow(string id, string title, string detail, bool needsName, string name, bool saved = false)
    {
        (Id, Title, Detail, NeedsName, Saved) = (id, title, detail, needsName && !saved, saved);
        Name = name;
        IsChecked = !saved;
    }

    /// <summary>Whether the tab has no unsaved changes (no checkbox; never checked).</summary>
    public bool Saved { get; }

    /// <summary>Has unsaved changes (checkbox shown); inverse of <see cref="Saved"/>.</summary>
    public bool NeedsSave => !Saved;

    public string SavedName => $"{Title} is saved";

    /// <summary>Tab id (ProjectTabHeader.Id).</summary>
    public string Id { get; }

    public string Title { get; }

    /// <summary>Project file path, or "Never saved".</summary>
    public string Detail { get; }

    /// <summary>Never saved: requires a name while checked.</summary>
    public bool NeedsName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsName), nameof(Ready))]
    private bool _isChecked = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Ready))]
    private string _name = "";

    /// <summary>Name box visible (never saved and checked).</summary>
    public bool ShowsName => NeedsName && IsChecked;

    /// <summary>Ready for Save: unchecked, previously saved, or named.</summary>
    public bool Ready => !IsChecked || !NeedsName || Name.Trim().Length > 0;

    /// <summary>Saved by Save Checked: checked with unsaved changes.</summary>
    public bool ToSave => IsChecked && !Saved;

    public string CheckName => $"Save {Title}";

    public string NameBoxName => $"Project Name for {Title}";
}

/// <summary>
/// Single dialog for all unsaved project tabs: on exit (<see cref="Purpose.Exit"/>: Save Checked &amp; Close, Keep Suspended for Next
/// Launch, Discard &amp; Close) or when Close Other Tabs hits two or more unsaved tabs (<see cref="Purpose.CloseOthers"/>: Save Checked
/// &amp; Close Tabs, Keep Unsaved Tabs Open, Discard &amp; Close Tabs). Cancel, Esc and title bar close abort.
/// </summary>
public partial class SaveTabsOnExitDialog : Window
{
    /// <summary>Dialog context.</summary>
    public enum Purpose
    {
        Exit,
        CloseOthers,
    }

    public SaveTabsOnExitDialog(IReadOnlyList<TabExitRow> rows, Purpose purpose)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        (Rows, Mode) = (rows, purpose);
        RowsList.ItemsSource = rows;

        // Save enablement tracks row changes (checkbox, name); unsubscribe on close.
        foreach (var row in rows)
            row.PropertyChanged += Row_PropertyChanged;
        Closed += (_, _) =>
        {
            foreach (var row in rows)
                row.PropertyChanged -= Row_PropertyChanged;
        };

        var (save, keep, discard) = Labels(purpose);
        foreach (var (button, label) in new[] { (SaveButton, save), (KeepButton, keep), (DiscardButton, discard) })
        {
            button.Content = label;
            AutomationProperties.SetName(button, label);
        }

        var count = rows.Count;
        if (purpose == Purpose.Exit)
        {
            // List every tab with work (saved ones marked saved); wording depends on how many need saving.
            (Title, HeadingText.Text, DetailText.Text) = ExitWords(rows.Count(r => !r.Saved), count);
            KeepButton.ToolTip = "Close HandPeg and keep every open tab as it is: they come back the next time HandPeg starts.";
            FooterText.Text = "In Settings > General > Projects and saving, the tabs can be kept between sessions without this question, or the question switched off.";
        }
        else
        {
            Title = "Close Other Tabs";
            HeadingText.Text = $"{count} of the other project tabs have unsaved changes. Save them before their tabs close?";
            DetailText.Text = "Save the checked tabs as projects, then close the other tabs; keep the tabs with unsaved changes open and close the rest; or close them all without saving. Unchecked tabs are not saved.";
            KeepButton.ToolTip = "Close only the other tabs that have no unsaved changes.";
            FooterText.Visibility = Visibility.Collapsed;
        }

        UpdateSave();
    }

    /// <summary>Listed tabs; unsaved ones start checked.</summary>
    public IReadOnlyList<TabExitRow> Rows { get; }

    public Purpose Mode { get; }

    public TabsExitChoice Choice { get; private set; } = TabsExitChoice.Cancel;

    /// <summary>
    /// Save enabled: some unsaved row checked and every checked never-saved row named. With all tabs saved, Save Checked &amp; Close
    /// just closes.
    /// </summary>
    public bool CanSave => Rows.All(r => r.Ready) && (Rows.Any(r => r.ToSave) || Rows.All(r => r.Saved));

    /// <summary>
    /// Exit title, heading and body (pure; tabs-exit-dialog golden) for <paramref name="unsaved"/> of <paramref name="count"/> listed
    /// tabs needing a save: all, some, or none (then keep or close).
    /// </summary>
    internal static (string Title, string Heading, string Detail) ExitWords(int unsaved, int count)
    {
        const string some = "Save the checked tabs as projects, keep every tab exactly as it is (with its undo steps) for the next time HandPeg starts, or close without saving. Unchecked tabs are not saved.";
        if (unsaved == 0)
        {
            return ("Open Project Tabs", count == 1 ? "1 project tab is open, saved. Keep it for the next time HandPeg starts?" : $"{count} project tabs are open, all saved. Keep them for the next time HandPeg starts?",
                "Keep every tab exactly as it is (with its undo steps) for the next time HandPeg starts, or close them: each is saved as a project.");
        }

        if (unsaved == count)
            return ("Unsaved Project Tabs", count == 1 ? "1 project tab has unsaved changes. Save it before closing?" : $"{count} project tabs have unsaved changes. Save them before closing?", some);

        return ("Unsaved Project Tabs", unsaved == 1 ? $"1 of {count} open project tabs has unsaved changes. Save it before closing?" : $"{unsaved} of {count} open project tabs have unsaved changes. Save them before closing?",
            some);
    }

    /// <summary>Save/Keep/Discard button labels per purpose (pure; tabs-exit-dialog golden).</summary>
    internal static (string Save, string Keep, string Discard) Labels(Purpose purpose) =>
        purpose == Purpose.Exit
            ? ("Save Checked & Close", "Keep Suspended for Next Launch", "Discard & Close")
            : ("Save Checked & Close Tabs", "Keep Unsaved Tabs Open", "Discard & Close Tabs");

    /// <summary>
    /// Applies an answer (buttons; goldens call this directly). Save only while <see cref="CanSave"/>. Closes a shown dialog (Cancel too).
    /// </summary>
    internal void Choose(TabsExitChoice choice)
    {
        if (choice == TabsExitChoice.SaveChecked && !CanSave)
            return;
        Choice = choice;
        if (IsVisible)
            DialogResult = choice != TabsExitChoice.Cancel;
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabExitRow.IsChecked) or nameof(TabExitRow.Name))
            UpdateSave();
    }

    private void UpdateSave() => SaveButton.IsEnabled = CanSave;

    private void Save_Click(object sender, RoutedEventArgs e) => Choose(TabsExitChoice.SaveChecked);

    private void Keep_Click(object sender, RoutedEventArgs e) => Choose(TabsExitChoice.KeepSuspended);

    private void Discard_Click(object sender, RoutedEventArgs e) => Choose(TabsExitChoice.Discard);
}
