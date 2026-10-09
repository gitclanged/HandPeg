using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using Microsoft.Win32;

namespace HandPegApp;

/// <summary>
/// Edits a copy of the application settings; nothing changes unless the user saves.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Current.Clone();
    private readonly MainViewModel _viewModel;

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _settings;

        // Nothing is installed while an encode, a preview or a transcription is using the tools.
        Dependencies.AppIsBusy = viewModel.IsBusy;
        Dependencies.Changed += () =>
        {
            viewModel.OnDependenciesChanged();
            UpdateWhisperStatus();
        };

        YtDlpDefaultText.Text = $"Default: {AppSettings.DefaultYtDlpReleaseUrl}";
        FfmpegDefaultText.Text = $"Default: {AppSettings.DefaultFfmpegReleaseUrl}";
        HandPegUpdateText.Text = $"Updates itself from {AppSettings.HandPegRepositoryUrl} when it was installed with its installer.";
        WhisperDefaultText.Text = $"Default: {AppSettings.DefaultWhisperReleaseUrl}";

        // Two-way choices stored as a flag each.

        TimeFormatBox.SelectedIndex = _settings.ShowTimesAsFrames ? 1 : 0;
        SplashBox.SelectedIndex = Math.Clamp(_settings.SplashPresetCount, 0, 5);
        DefaultModeBox.ItemsSource = AppSettings.DefaultModes;
        if (!AppSettings.DefaultModes.Contains(_settings.DefaultMode))
            _settings.DefaultMode = AppSettings.LastUsedMode;
        DefaultModeBox.SelectedItem = _settings.DefaultMode;
        CurrentModeText.Text = $"You are in {_settings.UiMode} now.";

        // The styles the launch window can show, with the ones picked for it selected.
        var styles = StyleFile.All();
        SplashStyleList.ItemsSource = styles;
        foreach (var style in styles.Where(s => _settings.SplashStylePresets.Contains(s.FileName, StringComparer.OrdinalIgnoreCase)))
            SplashStyleList.SelectedItems.Add(style);

        // A model saved under a name that is no longer offered still shows, so it is not silently replaced.
        var models = DependencyUpdater.WhisperModels.ToList();
        if (!models.Contains(_settings.WhisperModel) && !string.IsNullOrWhiteSpace(_settings.WhisperModel))
            models.Add(_settings.WhisperModel);
        ThemeBox.ItemsSource = ThemeManager.Themes;
        if (!ThemeManager.Themes.Contains(_settings.Theme))
            _settings.Theme = ThemeManager.FollowSystem;
        ThemeBox.SelectedItem = _settings.Theme;

        PresetBarBox.ItemsSource = AppSettings.PresetBarLocations;
        if (!AppSettings.PresetBarLocations.Contains(_settings.PresetBarLocation))
            _settings.PresetBarLocation = AppSettings.PresetBarInSummary;
        PresetBarBox.SelectedItem = _settings.PresetBarLocation;

        WhisperModelBox.ItemsSource = models;
        WhisperModelBox.SelectedItem = _settings.WhisperModel;

        // An install or a model download ends with IsBusy going back to false: time to look again at what is there.
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        UpdateWhisperStatus();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsBusy))
        {
            Dependencies.AppIsBusy = _viewModel.IsBusy;
            UpdateWhisperStatus();
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextBox target })
            return;

        var dialog = new OpenFileDialog { Title = "Select the executable", Filter = "Programs|*.exe|All files|*.*" };
        if (dialog.ShowDialog(this) == true)
            target.Text = dialog.FileName;
    }

    /// <summary>
    /// Shows the welcome window again. It saves on its own, so this window, which holds a copy of the
    /// settings from before, closes without saving; the main window then takes up what was chosen.
    /// </summary>
    private void RunFirstRun_Click(object sender, RoutedEventArgs e)
    {
        var firstRun = new FirstRunWindow { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, AppIsBusy = _viewModel.IsBusy };
        firstRun.DependenciesChanged += _viewModel.OnDependenciesChanged;
        firstRun.ShowDialog();
        _viewModel.OnSettingsSaved();
        DialogResult = false;
    }

    // ----- Search -----

    /// <summary>
    /// Shows only what matches the search box. An option is one of the things stacked down a tab (a tick box,
    /// a labelled row, a block of text); it matches when its own words, or its tool tip, or the title of the
    /// section it is under, contain what was typed. Tabs left with nothing are hidden.
    /// </summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        TabItem? firstMatch = null;
        foreach (var tab in SettingsTabs.Items.OfType<TabItem>())
        {
            var panel = (tab.Content is ScrollViewer scroller ? scroller.Content : tab.Content) as Panel;
            if (panel is null)
                continue;

            var any = false;
            FrameworkElement? title = null;
            var titleMatches = false;
            foreach (var child in panel.Children.OfType<FrameworkElement>())
            {
                if (child is TextBlock heading && ReferenceEquals(heading.Style, FindResource("SectionTitle")))
                {
                    (title, titleMatches) = (heading, Contains(heading.Text, query));
                    heading.Visibility = query.Length == 0 || titleMatches ? Visibility.Visible : Visibility.Collapsed;
                    any |= titleMatches && query.Length > 0;
                    continue;
                }

                var matches = query.Length == 0 || titleMatches || Contains(TextOf(child), query);
                child.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                if (matches && query.Length > 0)
                {
                    any = true;
                    if (title is not null)
                        title.Visibility = Visibility.Visible;
                }
            }

            tab.Visibility = query.Length == 0 || any ? Visibility.Visible : Visibility.Collapsed;
            if (any)
                firstMatch ??= tab;
        }

        // The tab that was open may just have been hidden.
        if (query.Length > 0 && firstMatch is not null && SettingsTabs.SelectedItem is TabItem { Visibility: not Visibility.Visible })
            SettingsTabs.SelectedItem = firstMatch;
    }

    private static bool Contains(string text, string query) => text.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every word an option shows or explains itself with: its text, its content, its tool tip, and those of what is inside it.</summary>
    private static string TextOf(DependencyObject element)
    {
        var text = new System.Text.StringBuilder();
        void Collect(DependencyObject item)
        {
            switch (item)
            {
                case TextBlock block:
                    text.Append(block.Text).Append(' ');
                    break;
                case ContentControl { Content: string content }:
                    text.Append(content).Append(' ');
                    break;
            }

            if (item is FrameworkElement { ToolTip: string tip })
                text.Append(tip).Append(' ');
            if (item is FrameworkElement named)
                text.Append(System.Windows.Automation.AutomationProperties.GetName(named)).Append(' ');

            foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
                Collect(child);
        }

        Collect(element);
        return text.ToString();
    }

    // ----- whisper.cpp -----

    private void WhisperModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WhisperModelBox.SelectedItem is string model)
        {
            _settings.WhisperModel = model;

            // The list above offers to download the model chosen here.
            if (IsLoaded)
                Dependencies.PreferModel(model);
        }

        UpdateWhisperStatus();
    }

    // After the binding has put the new text into the settings.
    private void WhisperPathBox_TextChanged(object sender, TextChangedEventArgs e) => Dispatcher.BeginInvoke(UpdateWhisperStatus);


    /// <summary>Says what is installed: the program, and the model selected in the box.</summary>
    private void UpdateWhisperStatus()
    {
        // Raised once while the window is still being built.
        if (WhisperStatusText is null)
            return;

        // The box may hold a path not saved yet; what counts for the status is what is typed there now.
        var custom = _settings.WhisperPath.Trim().Trim('"');
        var program = custom.Length > 0
            ? (File.Exists(custom) ? "using your own whisper.exe" : "your whisper.exe was not found")
            : File.Exists(DependencyUpdater.WhisperPath) ? "whisper.exe installed" : "whisper.exe not installed";
        var model = WhisperModelBox.SelectedItem as string ?? "";
        var modelFile = new FileInfo(DependencyUpdater.GetWhisperModelPath(model.Length > 0 ? model : "none"));
        var modelState = model.Length == 0 ? "no model selected"
            : modelFile.Exists ? $"model downloaded ({modelFile.Length / 1048576.0:0} MB)"
            : "model not downloaded";

        WhisperStatusText.Text = $"{program}; {modelState}";
    }

    // ----- Backup & Export -----

    private const string BackupFileFilter = "Text file (JSON)|*.txt;*.json|All files|*.*";

    /// <summary>
    /// Where the export and import dialogs open: the HandPeg folder in Documents, above Projects and Layouts.
    /// Not Projects itself: an export saved there would be listed as a project.
    /// </summary>
    private static string BackupFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UserRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return AppPaths.UserRoot;
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e) =>
        Export("Export Settings & Presets", $"HandPeg settings {DateTime.Now:yyyy-MM-dd}.txt", path =>
        {
            BackupExporter.ExportSettingsAndPresets(path);
            return $"Settings and presets exported to {path}";
        });

    private void ExportProjects_Click(object sender, RoutedEventArgs e) =>
        Export("Export All Projects", $"HandPeg projects {DateTime.Now:yyyy-MM-dd}.txt", path =>
        {
            var (exported, skipped) = BackupExporter.ExportProjects(path);
            return $"{exported} project{(exported == 1 ? "" : "s")} exported to {path}"
                   + (skipped > 0 ? $" ({skipped} unreadable file{(skipped == 1 ? "" : "s")} left out)" : "");
        });

    /// <summary>Asks where to save, runs the export and reports how it went.</summary>
    private void Export(string title, string fileName, Func<string, string> export)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = "Text file (JSON)|*.txt|All files|*.*", FileName = fileName, DefaultExt = "txt", InitialDirectory = BackupFolder() };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            ExportStatusText.Text = export(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ExportStatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            ExportStatusText.Text = "Wait for the running operation to finish before importing settings.";
            return;
        }

        var dialog = new OpenFileDialog { Title = "Import Settings & Presets", Filter = BackupFileFilter, InitialDirectory = BackupFolder() };
        if (dialog.ShowDialog(this) != true)
            return;

        int presets;
        try
        {
            presets = BackupImporter.ImportSettingsAndPresets(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            ExportStatusText.Text = $"Import failed, nothing was changed: {ex.Message}";
            return;
        }

        // What is in memory is now older than what is on disk. Both are read again, so that nothing the
        // application saves between now and the restart writes the old state back over the import.
        AppSettings.Reload();
        _viewModel.ReloadPresets();
        _viewModel.OnSettingsSaved();
        _viewModel.StatusText = $"Imported the settings and {presets} preset{(presets == 1 ? "" : "s")}.";

        MessageBox.Show(this,
            $"The settings and {presets} preset{(presets == 1 ? "" : "s")} were imported. The files they replaced are kept as "
            + "appsettings.json.bak and presets.json.bak.\n\nRestart HandPeg to apply all the changes. This window will now close.",
            "HandPeg", MessageBoxButton.OK, MessageBoxImage.Information);

        // Closed without its Save: this window still holds the settings from before the import.
        DialogResult = false;
    }

    private void ImportProjects_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import Projects", Filter = "Project files|*.hproj;*.txt|All files|*.*", Multiselect = true, InitialDirectory = BackupFolder() };
        if (dialog.ShowDialog(this) != true)
            return;

        var (imported, skipped) = BackupImporter.ImportProjects(dialog.FileNames);
        ExportStatusText.Text = $"{imported} project{(imported == 1 ? "" : "s")} imported into {ProjectStore.Folder}"
                                + (skipped > 0 ? $" ({skipped} left out: not a project, or already there)" : "");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Catch a mistyped path here rather than as a puzzling failure at the next download or encode.
        foreach (var path in new[] { _settings.YtDlpPath, _settings.FfmpegPath, _settings.FfprobePath, _settings.WhisperPath })
        {
            var cleaned = path.Trim().Trim('"');
            if (cleaned.Length > 0 && !File.Exists(cleaned))
            {
                ErrorText.Text = $"File not found: {cleaned}";
                return;
            }
        }

        foreach (var url in new[] { _settings.YtDlpReleaseUrl, _settings.FfmpegReleaseUrl, _settings.WhisperReleaseUrl })
        {
            var trimmed = url.Trim();
            if (trimmed.Length > 0 && !(Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
            {
                ErrorText.Text = $"Not an https address: {trimmed}";
                return;
            }
        }


        _settings.ShowTimesAsFrames = TimeFormatBox.SelectedIndex == 1;
        _settings.SplashPresetCount = Math.Max(SplashBox.SelectedIndex, 0);
        _settings.SplashStylePresets = SplashStyleList.SelectedItems.OfType<StyleFile>().Select(s => s.FileName).ToList();

        // The smart rules and the default preset are edited on the main window's Automation tab, and saved
        // there as they change. This copy was made when the window opened; take theirs as they are now.
        _settings.SmartRules = AppSettings.Current.SmartRules;
        _settings.DefaultPreset = AppSettings.Current.DefaultPreset;

        try
        {
            _settings.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = $"Could not write {AppSettings.FilePath}: {ex.Message}";
            return;
        }

        DialogResult = true;
    }
}
