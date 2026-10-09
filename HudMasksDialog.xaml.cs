using System.Windows;
using System.Windows.Controls;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>Chooses the game whose HUD is to be taken apart into layers.</summary>
public partial class HudMasksDialog : Window
{
    public HudMasksDialog()
    {
        InitializeComponent();
        GameList.ItemsSource = HudLibrary.Games;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>The game that was chosen, once the dialog has been confirmed.</summary>
    public HudGame? Game => GameList.SelectedItem as HudGame;

    /// <summary>Whether the pieces are to be laid out for a tall frame rather than back where they came from.</summary>
    public bool Vertical
    {
        get => VerticalBox.IsChecked == true;
        set => VerticalBox.IsChecked = value;
    }

    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AddButton.IsEnabled = Game is not null;
        AddButton.Content = Game is { } game ? $"Add {game.Pieces.Count} Layers" : "Add Layers";
    }

    private void Add_Click(object sender, RoutedEventArgs e) => DialogResult = Game is not null;
}
