using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;
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
    private readonly System.Collections.ObjectModel.ObservableCollection<SquishPreset> _squishPresets;

    private void AddSquishPreset_Click(object sender, RoutedEventArgs e)
    {
        var preset = new SquishPreset { Name = "New Preset", TargetSizeMb = 10, AudioKbps = 96 };
        _settings.SquisherPresets.Add(preset);
        _squishPresets.Add(preset);
    }

    private static IEnumerable<TabItem> FindTabs(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TabControl tabs)
            {
                foreach (var tab in tabs.Items.OfType<TabItem>())
                    yield return tab;
                yield break;
            }

            foreach (var tab in FindTabs(child))
                yield return tab;
        }
    }

    private void ScrollbarSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded)
            Application.Current.Resources["GlobalScrollBarThickness"] = Math.Clamp(e.NewValue, 4, 16);
    }

    private void MoveSquishPresetUp_Click(object sender, RoutedEventArgs e) => MoveSquishPreset(sender, -1);

    private void MoveSquishPresetDown_Click(object sender, RoutedEventArgs e) => MoveSquishPreset(sender, 1);

    // The order of the list is the order on the startup dialog, which shows only the first few.
    private void MoveSquishPreset(object sender, int by)
    {
        if (sender is not FrameworkElement { DataContext: SquishPreset preset })
            return;

        var (from, to) = (_squishPresets.IndexOf(preset), _squishPresets.IndexOf(preset) + by);
        if (from < 0 || to < 0 || to >= _squishPresets.Count)
            return;

        _squishPresets.Move(from, to);
        _settings.SquisherPresets.RemoveAt(from);
        _settings.SquisherPresets.Insert(to, preset);
    }

    // ----- The drop overlay's presets: two ordered lists, as the Squisher's is -----

    private List<string> _allStyles = [], _allEncodings = [];
    private System.Collections.ObjectModel.ObservableCollection<string> _overlayStyles = [], _overlayEncodings = [];

    // Which list a button belongs to, by the name it carries: the styles' or the encoding presets'.
    private (System.Collections.ObjectModel.ObservableCollection<string> List, List<string> All, ComboBox Add) OverlayListOf(object sender) =>
        sender is FrameworkElement { Tag: "OverlayEncodingList" }
            ? (_overlayEncodings, _allEncodings, OverlayEncodingAddBox)
            : (_overlayStyles, _allStyles, OverlayStyleAddBox);

    private void MoveOverlayPresetUp_Click(object sender, RoutedEventArgs e) => MoveOverlayPreset(sender, -1);

    private void MoveOverlayPresetDown_Click(object sender, RoutedEventArgs e) => MoveOverlayPreset(sender, 1);

    // The order of the list is the order on the overlay, which shows only the first so many.
    private void MoveOverlayPreset(object sender, int by)
    {
        if (sender is not FrameworkElement { DataContext: string name })
            return;

        var list = OverlayListOf(sender).List;
        var (from, to) = (list.IndexOf(name), list.IndexOf(name) + by);
        if (from >= 0 && to >= 0 && to < list.Count)
            list.Move(from, to);
    }

    private void RemoveOverlayPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string name })
        {
            OverlayListOf(sender).List.Remove(name);
            RefreshOverlayAddBoxes();
        }
    }

    private void AddOverlayPreset_Click(object sender, RoutedEventArgs e)
    {
        var (list, _, add) = OverlayListOf(sender);
        if (add.SelectedItem is string name && !list.Contains(name))
        {
            list.Add(name);
            RefreshOverlayAddBoxes();
        }
    }

    // An empty list is the overlay's own choice: the most recent styles, or the first encoding presets.
    private void ResetOverlayPresets_Click(object sender, RoutedEventArgs e)
    {
        OverlayListOf(sender).List.Clear();
        RefreshOverlayAddBoxes();
    }

    // Each Add box offers what is not on its list yet.
    private void RefreshOverlayAddBoxes()
    {
        foreach (var (list, all, add) in new[] { (_overlayStyles, _allStyles, OverlayStyleAddBox), (_overlayEncodings, _allEncodings, OverlayEncodingAddBox) })
        {
            var left = all.Where(name => !list.Contains(name)).ToList();
            add.ItemsSource = left;
            add.SelectedItem = left.FirstOrDefault();
        }
    }

    // A box for a whole number takes digits and nothing else.
    private void DigitsOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);

    private void RestoreSquishPresets_Click(object sender, RoutedEventArgs e)
    {
        _settings.SquisherPresets = SquishPreset.Defaults();
        _squishPresets.Clear();
        foreach (var preset in _settings.SquisherPresets)
            _squishPresets.Add(preset);
    }

    private void RemoveSquishPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SquishPreset preset })
        {
            _settings.SquisherPresets.Remove(preset);
            _squishPresets.Remove(preset);
        }
    }
    private readonly MainViewModel _viewModel;

    /// <summary>One entry of a drop-down whose saved value is not what it shows: an encoder by its FFmpeg name, or blank for the built-in default.</summary>
    public sealed record Choice(string Name, string Display)
    {
        public override string ToString() => Display;
    }

    // ----- About -----

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorText.Text = $"The page could not be opened: {ex.Message}";
        }

        e.Handled = true;
    }

    /// <summary>Check for Updates: asks now, and says here what came of it. The main window offers the restart once this window is closed.</summary>
    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking...";
        var (result, detail) = await AppUpdater.CheckNowAsync();
        UpdateStatusText.Text = result switch
        {
            AppUpdater.CheckResult.UpToDate => $"HandPeg {detail} is the newest release.",
            AppUpdater.CheckResult.Ready => $"HandPeg {detail} is downloaded. It goes in when HandPeg is restarted: use Restart to Update in the main window, or just close HandPeg.",
            AppUpdater.CheckResult.Available => $"HandPeg {detail} is available. This copy does not update itself: get it from {AppUpdater.ReleasesPage}",
            _ => $"The check could not be made: {detail}",
        };
        CheckUpdateButton.IsEnabled = true;
    }

    // ----- Dependencies, from the General tab -----

    /// <summary>Update Dependencies: the dependency panel's Install / Update All, run from here, its progress shown under the button.</summary>
    private async void UpdateDependencies_Click(object sender, RoutedEventArgs e)
    {
        if (Dependencies.IsRunning)
            return;

        (UpdateDependenciesButton.IsEnabled, DependencyStatusRow.Visibility, DependencyProgress.Value) = (false, Visibility.Visible, 0);
        try
        {
            await Dependencies.InstallAsync();
        }
        finally
        {
            UpdateDependenciesButton.IsEnabled = true;
        }
    }

    private void ShowDependencyMessage(string text, bool isProblem)
    {
        DependencyStatusText.Text = text;
        DependencyStatusText.SetResourceReference(TextBlock.ForegroundProperty, isProblem ? "WarningTextBrush" : "MutedTextBrush");
    }

    /// <summary>Manage Dependencies: the tab with the full list, the tool paths and the update URLs, scrolled to the list.</summary>
    private void ManageDependencies_Click(object sender, RoutedEventArgs e)
    {
        // A search that hides the tab is let go of first.
        if (SearchBox.Text.Length > 0)
            SearchBox.Text = "";

        DependencyObject? at = Dependencies;
        while (at is not null and not TabItem)
            at = LogicalTreeHelper.GetParent(at);
        if (at is TabItem tab)
        {
            SettingsTabs.SelectedItem = tab;
            Dispatcher.BeginInvoke(() => Dependencies.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    // ----- Auto-Captions: the style captions start with -----

    private void DefaultCaptionStyle_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CaptionStyleDialog(_settings.DefaultCaptionStyle ?? new CaptionStyle()) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _settings.DefaultCaptionStyle = dialog.EditedStyle;
            ShowDefaultCaptionStyle();
        }
    }

    private void ResetDefaultCaptionStyle_Click(object sender, RoutedEventArgs e)
    {
        _settings.DefaultCaptionStyle = null;
        ShowDefaultCaptionStyle();
    }

    private void ShowDefaultCaptionStyle() => DefaultCaptionStyleText.Text = _settings.DefaultCaptionStyle is { } style
        ? $"{style.FontName} {style.FontSize}, {style.Animation}, up to {style.MaxWordsPerLine} words"
        : "the built-in style";

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _settings;

        // The Squisher's presets: the same objects as in the copy of the settings being edited, listed so that rows can come and go.
        _squishPresets = new System.Collections.ObjectModel.ObservableCollection<SquishPreset>(_settings.SquisherPresets);
        SquishPresetList.ItemsSource = _squishPresets;

        // Every tab scrolls when the window is too short for it: a tab whose content is not already in a scroller is put in one.
        foreach (var tab in FindTabs(this))
        {
            if (tab.Content is UIElement content and not ScrollViewer)
            {
                tab.Content = null;
                tab.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = content, Focusable = false };
            }
        }

        // The scrollbars follow the slider as it moves; closed without saving, they go back to what is saved.
        Closed += (_, _) => Application.Current.Resources["GlobalScrollBarThickness"] = Math.Clamp(AppSettings.Current.ScrollbarThickness, 4, 16);

        // Nothing is installed while an encode, a preview or a transcription is using the tools.
        Dependencies.AppIsBusy = viewModel.IsBusy;

        // Update Dependencies on the General tab shows what the panel says, and how far it has got, under its button.
        Dependencies.MessageShown += ShowDependencyMessage;
        Dependencies.ProgressChanged += value => DependencyProgress.Value = value;
        Closed += (_, _) => Dependencies.CancelAll();
        Dependencies.Changed += () =>
        {
            viewModel.OnDependenciesChanged();
            UpdateWhisperStatus();
        };

        YtDlpDefaultText.Text = $"Default: {AppSettings.DefaultYtDlpReleaseUrl}";
        FfmpegDefaultText.Text = $"Default: {AppSettings.DefaultFfmpegReleaseUrl}";

        // About: which HandPeg this is, and whether it is a copy that can update itself.
        AboutText.Text = $"Version {AppUpdater.CurrentVersion}  ·  " + (AppPaths.IsSelfContained
            ? "self-contained copy: it keeps everything beside the program, and is updated by replacing its folder."
            : "updates itself from its GitHub releases when it was put in by the installer; a check is made each time it starts.");
        RepositoryLink.NavigateUri = new Uri(AppSettings.HandPegRepositoryUrl);
        RepositoryLink.Inlines.Add(AppSettings.HandPegRepositoryUrl);
        if (AppUpdater.ReadyVersion is { } ready)
            UpdateStatusText.Text = $"HandPeg {ready} is downloaded and goes in when HandPeg is restarted.";
        WhisperDefaultText.Text = $"Default: {AppSettings.DefaultWhisperReleaseUrl}";

        // Two-way choices stored as a flag each.

        TimeFormatBox.SelectedIndex = _settings.ShowTimesAsFrames ? 1 : 0;
        DefaultModeBox.ItemsSource = AppSettings.DefaultModes;
        if (!AppSettings.DefaultModes.Contains(_settings.DefaultMode))
            _settings.DefaultMode = AppSettings.LastUsedMode;
        DefaultModeBox.SelectedItem = _settings.DefaultMode;
        CurrentModeText.Text = $"You are in {_settings.UiMode} now.";

        // What the drop overlay offers, in order: the lists start as the overlay stands now, the picked ones or
        // the ones it falls back on, so that what is seen here is what it shows.
        _allStyles = StyleFile.All().Select(s => s.Name).ToList();
        _allEncodings = viewModel.Presets.Select(p => p.Name).ToList();
        var styles = _settings.SplashStylePresets.Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(_allStyles.Contains).Distinct().ToList();
        _overlayStyles = new(styles.Count > 0 ? styles : _allStyles.Take(8));
        var encodings = _settings.OverlayEncodingPresets.Where(_allEncodings.Contains).Distinct().ToList();
        _overlayEncodings = new(encodings.Count > 0 ? encodings : _allEncodings.Take(8));
        (OverlayStyleList.ItemsSource, OverlayEncodingList.ItemsSource) = (_overlayStyles, _overlayEncodings);
        RefreshOverlayAddBoxes();

        // A model saved under a name that is no longer offered still shows, so it is not silently replaced.
        var models = DependencyUpdater.WhisperModels.ToList();
        if (!models.Contains(_settings.WhisperModel) && !string.IsNullOrWhiteSpace(_settings.WhisperModel))
            models.Add(_settings.WhisperModel);
        ThemeBox.ItemsSource = ThemeManager.Themes;
        ResetBox.ItemsSource = AppSettings.ResetTargets;
        if (!ThemeManager.Themes.Contains(_settings.Theme))
            _settings.Theme = ThemeManager.FollowSystem;
        ThemeBox.SelectedItem = _settings.Theme;

        // The graphics cards there are now; a card chosen earlier that has since gone is Automatic again.
        var adapters = GpuAdapters.All;
        if (adapters.All(a => a.Index != _settings.HardwareDecodeAdapter))
            _settings.HardwareDecodeAdapter = GpuAdapter.Automatic.Index;
        DecodeAdapterBox.ItemsSource = adapters.Prepend(GpuAdapter.Automatic).ToList();
        DecodeAdapterBox.SelectedValue = _settings.HardwareDecodeAdapter;

        PresetBarBox.ItemsSource = AppSettings.PresetBarLocations;
        if (!AppSettings.PresetBarLocations.Contains(_settings.PresetBarLocation))
            _settings.PresetBarLocation = AppSettings.PresetBarInSummary;
        PresetBarBox.SelectedItem = _settings.PresetBarLocation;

        WhisperModelBox.ItemsSource = models;
        WhisperModelBox.SelectedItem = _settings.WhisperModel;

        // What whisper can be told is spoken; a language saved under a name no longer offered is English again.
        DefaultLanguageBox.ItemsSource = MainViewModel.KnownWhisperLanguages;
        if (!MainViewModel.KnownWhisperLanguages.Contains(_settings.WhisperDefaultLanguage))
            _settings.WhisperDefaultLanguage = "en";
        DefaultLanguageBox.SelectedItem = _settings.WhisperDefaultLanguage;
        ShowDefaultCaptionStyle();

        // The encoders a new window can start with: the ones found on this computer, and whichever was saved,
        // found or not (a hardware encoder is only listed once the check for it has finished).
        var video = viewModel.VideoEncoders.Where(e => e.Family != EncoderFamily.Copy).Select(e => new Choice(e.Name, e.DisplayName)).ToList();
        if (_settings.DefaultVideoEncoder.Length > 0 && video.All(c => c.Name != _settings.DefaultVideoEncoder))
            video.Add(new Choice(_settings.DefaultVideoEncoder, _settings.DefaultVideoEncoder));
        DefaultVideoBox.ItemsSource = video.Prepend(new Choice("", "Built-in default (Copy)")).ToList();
        DefaultAudioBox.ItemsSource = AudioTrack.AllCodecs.Select(c => new Choice(c, c)).Prepend(new Choice("", "Built-in default (Copy)")).ToList();
        DefaultBitrateBox.ItemsSource = AudioTrack.AllBitrates.Select(b => new Choice(b, b)).Prepend(new Choice("", "Built-in default")).ToList();
        (DefaultVideoBox.SelectedValue, DefaultAudioBox.SelectedValue, DefaultBitrateBox.SelectedValue) =
            (_settings.DefaultVideoEncoder, _settings.DefaultAudioEncoder, _settings.DefaultAudioBitrate);

        // The automation rows show the view model's rules, and the choices of the grid's two drop-down columns.
        DefaultPresetRow.DataContext = RulesGrid.DataContext = viewModel;
        RuleTypeColumn.ItemsSource = SmartRule.Types;
        PresetColumn.ItemsSource = viewModel.PresetNames;

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

                // A line written in parts (a shortcut's key, then what it does) keeps its words in its runs.
                case System.Windows.Documents.Run run:
                    text.Append(run.Text).Append(' ');
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

    // ----- Automation: smart rules -----
    // The rules are saved as they are edited; the tab has no Save button of its own, and Cancel does not undo them.

    private void AddFolderRule_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Videos in this folder get the preset" };
        if (dialog.ShowDialog(this) == true)
            SelectRule(_viewModel.AddSmartRule(SmartRule.Folder, dialog.FolderName), edit: false);
    }

    // Added with a word to replace, already in edit so it can be typed over at once.
    private void AddKeywordRule_Click(object sender, RoutedEventArgs e) => SelectRule(_viewModel.AddSmartRule(SmartRule.Keyword, "keyword"), edit: true);

    private void AddExtensionRule_Click(object sender, RoutedEventArgs e) => SelectRule(_viewModel.AddSmartRule(SmartRule.Extension, "*.mkv"), edit: true);

    private void SelectRule(SmartRule? rule, bool edit)
    {
        if (rule is null)
        {
            // Why not is said in the main window's status bar, which this window covers.
            ErrorText.Text = _viewModel.StatusText;
            return;
        }

        RulesGrid.SelectedItem = rule;
        RulesGrid.ScrollIntoView(rule);
        if (edit)
        {
            RulesGrid.CurrentCell = new DataGridCellInfo(rule, RulesGrid.Columns[1]);
            RulesGrid.Focus();
            RulesGrid.BeginEdit();
        }
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is SmartRule rule)
            _viewModel.RemoveSmartRule(rule);
    }

    // The edit reaches the rule once this event has returned, so the save waits for that.
    private void RulesGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
            Dispatcher.BeginInvoke(_viewModel.SaveAutomation, System.Windows.Threading.DispatcherPriority.Background);
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
        _settings.SplashStylePresets = _overlayStyles.Select(name => name + StyleFile.Extension).ToList();
        _settings.OverlayEncodingPresets = [.. _overlayEncodings];

        // The smart rules and the default preset are edited on the Automation tab, which saves them as they
        // change. This copy was made when the window opened; take theirs as they are now.
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
