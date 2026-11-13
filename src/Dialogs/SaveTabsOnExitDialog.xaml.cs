using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>What was answered in the one dialog for every unsaved project tab (Pass 5.5A).</summary>
public enum TabsExitChoice
{
    /// <summary>Cancel, Esc or the title bar's close: HandPeg stays open (or the tabs stay), nothing done.</summary>
    Cancel,

    /// <summary>The checked tabs saved, then closed with the rest (the unchecked tabs let go).</summary>
    SaveChecked,

    /// <summary>Closing HandPeg: every tab kept for the next start as it is. Close Other Tabs: the unsaved tabs stay open.</summary>
    KeepSuspended,

    /// <summary>Every unsaved tab let go, unsaved.</summary>
    Discard,
}

/// <summary>
/// One project tab as the dialog lists it (Pass 5.5A): an unsaved one checked to be saved, with a name to save it under when it was never
/// saved; the 5.5A addenda: a saved one (<see cref="Saved"/>) shown as saved, with nothing to tick.
/// </summary>
public sealed partial class TabExitRow : ObservableObject
{
    public TabExitRow(string id, string title, string detail, bool needsName, string name, bool saved = false)
    {
        (Id, Title, Detail, NeedsName, Saved) = (id, title, detail, needsName && !saved, saved);
        Name = name;
        IsChecked = !saved;
    }

    /// <summary>Whether the tab has nothing unsaved (its project file holds it all): no save needed, so no box to tick (and never checked).</summary>
    public bool Saved { get; }

    /// <summary>Whether it has work to save (its box shown): the opposite of <see cref="Saved"/>.</summary>
    public bool NeedsSave => !Saved;

    public string SavedName => $"{Title} is saved";

    /// <summary>The tab's id (ProjectTabHeader.Id).</summary>
    public string Id { get; }

    public string Title { get; }

    /// <summary>Its project file, or "Never saved".</summary>
    public string Detail { get; }

    /// <summary>Whether it was never saved: a name is asked for while it is checked.</summary>
    public bool NeedsName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsName), nameof(Ready))]
    private bool _isChecked = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Ready))]
    private string _name = "";

    /// <summary>Whether its name box is shown: never saved, and checked.</summary>
    public bool ShowsName => NeedsName && IsChecked;

    /// <summary>Whether it is ready for Save: unchecked, saved before, or given a name.</summary>
    public bool Ready => !IsChecked || !NeedsName || Name.Trim().Length > 0;

    /// <summary>Whether Save Checked saves it: checked, with work to save.</summary>
    public bool ToSave => IsChecked && !Saved;

    public string CheckName => $"Save {Title}";

    public string NameBoxName => $"Project Name for {Title}";
}

/// <summary>
/// Every unsaved project tab in one dialog (Pass 5.5A): as HandPeg closes (<see cref="Purpose.Exit"/>: Save Checked &amp; Close, Keep
/// Suspended for Next Launch, Discard &amp; Close), or as Close Other Tabs closes two or more unsaved tabs (<see cref="Purpose.CloseOthers"/>:
/// Save Checked &amp; Close Tabs, Keep Unsaved Tabs Open, Discard &amp; Close Tabs). Cancel, Esc and the title bar's close stay.
/// </summary>
public partial class SaveTabsOnExitDialog : Window
{
    /// <summary>What the dialog is asked for.</summary>
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

        // Save follows the rows (a box ticked, a name typed); let go of them as the dialog closes.
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
            // The 5.5A addenda: every tab holding work is listed, the saved ones as saved; the words follow how many need saving.
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

    /// <summary>The unsaved tabs listed, each checked to begin with.</summary>
    public IReadOnlyList<TabExitRow> Rows { get; }

    public Purpose Mode { get; }

    public TabsExitChoice Choice { get; private set; } = TabsExitChoice.Cancel;

    /// <summary>
    /// Whether Save is offered: a row with work to save checked, and every checked row never saved given a name. The 5.5A addenda: with
    /// every tab saved there is nothing to save, and Save Checked &amp; Close simply closes.
    /// </summary>
    public bool CanSave => Rows.All(r => r.Ready) && (Rows.Any(r => r.ToSave) || Rows.All(r => r.Saved));

    /// <summary>
    /// The title, heading and words under it as HandPeg closes (pure: tabs-exit-dialog), for <paramref name="unsaved"/> tabs with work to
    /// save among the <paramref name="count"/> listed: all of them unsaved, some, or none (every tab saved: keep them or close).
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

    /// <summary>The words of Save, Keep and Discard for a purpose (pure: tabs-exit-dialog).</summary>
    internal static (string Save, string Keep, string Discard) Labels(Purpose purpose) =>
        purpose == Purpose.Exit
            ? ("Save Checked & Close", "Keep Suspended for Next Launch", "Discard & Close")
            : ("Save Checked & Close Tabs", "Keep Unsaved Tabs Open", "Discard & Close Tabs");

    /// <summary>
    /// An answer given (the buttons; the goldens drive it here, never with clicks): Save only while <see cref="CanSave"/>. A dialog shown
    /// closes with it (Cancel too).
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
