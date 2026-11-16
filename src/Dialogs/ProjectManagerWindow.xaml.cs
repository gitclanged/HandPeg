using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

/// <summary>
/// Project Manager row: the project entry and its picture (baked thumbnail, else video poster), hover-scrubbable through the video's frames.
/// </summary>
public sealed partial class ProjectRow(ProjectEntry entry) : ObservableObject
{
    public ProjectEntry Entry { get; } = entry;

    /// <summary>Project name, used as the row's accessible name (screen readers, UIA) instead of the type name.</summary>
    public string Name => Entry.Name;

    public override string ToString() => Name;

    /// <summary>Displayed image: the poster, or the hovered frame while scrubbing.</summary>
    [ObservableProperty] private ImageSource? _picture;

    [ObservableProperty] private Visibility _pictureVisibility = Visibility.Collapsed;

    /// <summary>Resting image: the baked thumbnail when present, else the video poster.</summary>
    public BitmapSource? Poster { get; set; }

    /// <summary>Decoded baked thumbnail (Sequence at save time); null when absent.</summary>
    public BitmapSource? Baked { get; set; }

    /// <summary>Media Browser filmstrip cache file of the project's video, and its aspect.</summary>
    public string? Strip { get; set; }

    public double Shape { get; set; } = 16.0 / 9;

    /// <summary>Filmstrip frames, decoded on hover and released on leave.</summary>
    public IReadOnlyList<BitmapSource>? Frames { get; set; }

    public bool DecodingFrames { get; set; }
}

/// <summary>Project Manager modes.</summary>
public enum ProjectManagerMode
{
    /// <summary>New Project: name, folder, frame, rate, colour space, Game Preset and media.</summary>
    NewProject,

    /// <summary>Open / Manage Projects: save the current project, open or delete saved ones.</summary>
    Manage,

    /// <summary>Project Settings: frame, rate, colour space, pixel aspect and sample rate.</summary>
    Settings,
}

/// <summary>New Project Game Preset card: name, file (null for None) and tooltip.</summary>
public sealed record PresetCard(string Name, string? Path, string About)
{
    public override string ToString() => Name;
}

/// <summary>
/// Project Manager with three modes: New Project (returns a request the main window executes), Open / Manage Projects (save current,
/// list saved projects with 16:9 hover-scrubbable previews from the Media Browser cache when Show video &amp; hover previews is on), and
/// Project Settings for the open project.
/// </summary>
public partial class ProjectManagerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly CancellationTokenSource _closing = new();

    // Decode height: 72 DIP list picture at up to 2x DPI.
    private const int PictureHeight = 144;

    // Set after construction: XAML change handlers fire during InitializeComponent before later elements exist.
    private readonly bool _ready;

    /// <param name="focusName">Opened to save (Ctrl+S on a new project, Save As): focus and select the name box (Open / Manage).</param>
    /// <param name="mode">Initial mode (Open / Manage when opened to save).</param>
    public ProjectManagerWindow(MainViewModel viewModel, bool focusName = false, ProjectManagerMode mode = ProjectManagerMode.Manage)
    {
        InitializeComponent();
        _viewModel = viewModel;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        if (viewModel.CurrentProjectPath.Length > 0)
            NameBox.Text = Path.GetFileNameWithoutExtension(viewModel.CurrentProjectPath);
        else if (viewModel.NewProjectName.Length > 0)
            NameBox.Text = viewModel.NewProjectName;
        else if (viewModel.HasSource)
            NameBox.Text = Path.GetFileNameWithoutExtension(viewModel.LocalMediaPath);

        // New Project defaults: Settings orientation, Projects folder, first free name.
        NewFolderBox.Text = AppPaths.Projects;
        _offerTimer.Tick += OfferTimer_Tick;
        _offerTimer.Stop();
        OfferFreeName();
        (AppSettings.Current.DefaultVerticalVideo ? VerticalCard : WideCard).IsChecked = true;
        _ready = true;
        UpdateFrameSummary();
        ShowMode(focusName ? ProjectManagerMode.Manage : mode);

        if (focusName)
        {
            _closeOnSave = true;
            Loaded += (_, _) =>
            {
                NameBox.Focus();
                NameBox.SelectAll();
            };
            NameBox.KeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter)
                    return;
                e.Handled = true;
                Save_Click(NameBox, new RoutedEventArgs());
            };
        }

        PreviewsBox.IsChecked = AppSettings.Current.ShowProjectPreviews;
        RefreshList();

        // Refresh the list when projects are imported while open.
        ProjectStore.Changed += RefreshList;
        Closed += (_, _) =>
        {
            ProjectStore.Changed -= RefreshList;
            _offerTimer.Stop();
            _closing.Cancel();

            // Dispose the picker's timers, listing and images (up to 32 MB) with the dialog.
            if (_picker is { } picker)
            {
                picker.SelectedFilesChanged -= UpdatePicked;
                picker.Release();
                PickerHost.Child = null;
                _picker = null;
            }
        };
    }

    /// <summary>Project selected to open; loaded by the main window after this dialog closes.</summary>
    public string? ProjectToLoad { get; private set; }

    // Opened to save: closing after a save completes the request.
    private readonly bool _closeOnSave;

    /// <summary>
    /// Blank-project request (main window clears the session after confirming unsaved changes). + New Project now uses the New Project
    /// mode, whose request is <see cref="NewProjectToCreate"/>.
    /// </summary>
    public bool NewProjectRequested { get; private set; }

    /// <summary>Create Project request for the main window to execute after close; null for none.</summary>
    public NewProjectRequest? NewProjectToCreate { get; private set; }

    // ----- The modes -----

    // Current mode (null initially) and whether Game Preset cards were loaded.
    private ProjectManagerMode? _shownMode;
    private bool _presetsAsked;

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        ShowMode(ReferenceEquals(sender, NewModeButton) ? ProjectManagerMode.NewProject
            : ReferenceEquals(sender, SettingsModeButton) ? ProjectManagerMode.Settings
            : ProjectManagerMode.Manage);
    }

    /// <summary>Switches mode (panel and button); New Project builds its picker and cards on first show.</summary>
    private void ShowMode(ProjectManagerMode mode)
    {
        // Checking the mode button re-enters via Mode_Checked; guard so the mode applies once.
        if (_shownMode == mode)
            return;

        _shownMode = mode;
        NewProjectMode.Visibility = mode == ProjectManagerMode.NewProject ? Visibility.Visible : Visibility.Collapsed;
        ManageMode.Visibility = mode == ProjectManagerMode.Manage ? Visibility.Visible : Visibility.Collapsed;
        SettingsMode.Visibility = mode == ProjectManagerMode.Settings ? Visibility.Visible : Visibility.Collapsed;
        var button = mode switch
        {
            ProjectManagerMode.NewProject => NewModeButton,
            ProjectManagerMode.Settings => SettingsModeButton,
            _ => ManageModeButton,
        };
        if (button.IsChecked != true)
            button.IsChecked = true;

        CreateButton.IsDefault = mode == ProjectManagerMode.NewProject;
        MessageText.Text = "";
        switch (mode)
        {
            case ProjectManagerMode.NewProject:
                EnsurePicker();
                if (!_presetsAsked)
                {
                    _presetsAsked = true;
                    _ = LoadPresetCardsAsync();
                }

                break;
            case ProjectManagerMode.Settings:
                FillSettings();
                break;
        }
    }

    // ----- New Project -----

    // Media Browser in PickerMode, built on first New Project show.
    private Views.MediaBrowserPane? _picker;

    private void EnsurePicker()
    {
        if (_picker is not null)
            return;

        _picker = new Views.MediaBrowserPane(pickerMode: true) { HasVideo = () => false };
        _picker.SelectedFilesChanged += UpdatePicked;
        PickerHost.Child = _picker;
    }

    private void UpdatePicked()
    {
        var count = _picker?.SelectedFiles.Count ?? 0;
        ClearPickedButton.IsEnabled = count > 0;
        PickedText.Text = count == 0 ? "Click the clips to start with, in the order they go on the Sequence."
            : count == 1 ? $"1 file chosen: {Path.GetFileName(_picker!.SelectedFiles[0])}"
            : $"{count} files chosen, laid on the Sequence in the order chosen.";
    }

    private void ClearPicked_Click(object sender, RoutedEventArgs e) => _picker?.ClearSelectedFiles();

    /// <summary>Game Preset cards: None, then Styles folder presets (read off the UI thread), Overwatch 2 first when present.</summary>
    private async Task LoadPresetCardsAsync()
    {
        List<StyleFile> styles;
        try
        {
            styles = await Task.Run(() => StyleFile.All());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            styles = [];
        }

        if (_closing.IsCancellationRequested)
            return;

        var cards = new List<PresetCard> { new("None (Standard NLE)", null, "No Game Preset: the clips go on the Sequence as they are.") };
        cards.AddRange(styles.OrderBy(s => s.Name.StartsWith("Overwatch 2", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(s => new PresetCard(s.Name, s.Path, $"{s.Name}: its layout, Cutouts, colour and blur go on the first clip (or wait for it).")));
        PresetCards.ItemsSource = cards;
        PresetCards.SelectedIndex = 0;
    }

    /// <summary>On Game Preset selection, sets orientation from its layout (aspect remains editable).</summary>
    private void PresetCards_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || PresetCards.SelectedItem is not PresetCard { Path: { } path })
            return;

        if (_viewModel.ReadStyle(path)?.Layout?.Vertical is { } vertical)
        {
            if (vertical && WideCard.IsChecked == true)
                VerticalCard.IsChecked = true;
            else if (!vertical && VerticalCard.IsChecked == true)
                WideCard.IsChecked = true;
        }
    }

    // Format input changed: refresh the summary and Create Project's enabled state.
    private void Format_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready)
            UpdateFrameSummary();
    }

    private void Format_SelectionChanged(object sender, SelectionChangedEventArgs e) => Format_Changed(sender, e);

    private void Format_TextChanged(object sender, TextChangedEventArgs e) => Format_Changed(sender, e);

    private string ChosenAspect => VerticalCard.IsChecked == true ? NewProjectRequest.Vertical
        : SquareCard.IsChecked == true ? NewProjectRequest.Square
        : NewProjectRequest.Wide;

    /// <summary>Chosen frame: aspect at the resolution's short side, or the custom size; null for an incomplete custom size.</summary>
    private (int Width, int Height)? ChosenFrame()
    {
        var shortSide = ResolutionBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var side) ? side : 1080;
        if (shortSide > 0)
            return NewProjectRequest.FrameFor(ChosenAspect, shortSide);

        return int.TryParse(CustomWidthBox.Text.Trim(), out var width) && int.TryParse(CustomHeightBox.Text.Trim(), out var height)
               && width >= NewProjectRequest.SmallestSide && height >= NewProjectRequest.SmallestSide
            ? NewProjectRequest.FrameFor(ChosenAspect, 0, width, height)
            : null;
    }

    /// <summary>Chosen rate; null for Auto from First Clip.</summary>
    private Models.Timeline.FrameRate? ChosenRate() =>
        FrameRateBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var fps) && fps > 0
            ? Models.Timeline.FrameRate.FromFps(fps)
            : null;

    private Models.Timeline.ProjectColorSpace ChosenColorSpace =>
        ColorSpaceBox.SelectedIndex == 1 ? Models.Timeline.ProjectColorSpace.HdrPq : Models.Timeline.ProjectColorSpace.Sdr709;

    private void UpdateFrameSummary()
    {
        var custom = ResolutionBox.SelectedItem is ComboBoxItem { Tag: "0" };
        CustomSizePanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        var frame = ChosenFrame();
        var rate = ChosenRate() is { } chosen ? $"{chosen.Fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} fps" : "the first clip's frame rate";
        FrameSummary.Text = frame is { } f
            ? $"{f.Width} x {f.Height}{(f.Height > f.Width ? " (vertical)" : "")} at {rate}, {(ChosenColorSpace == Models.Timeline.ProjectColorSpace.Sdr709 ? "BT.709 SDR" : "HDR, exported tone-mapped to BT.709 SDR")}, 48 kHz."
            : $"Type a width and a height of at least {NewProjectRequest.SmallestSide} pixels.";
        CreateButton.IsEnabled = frame is not null;
    }

    // Last auto-offered name; re-offered for a new folder while the box still holds it (or is empty).
    private string _offeredName = "";

    // Debounce re-offering the free name while typing a location: per-keystroke folder scans (including half-typed share paths)
    // would block the UI thread.
    private readonly System.Windows.Threading.DispatcherTimer _offerTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private void NewFolder_TextChanged(object sender, TextChangedEventArgs e)
    {
        _offerTimer.Stop();
        _offerTimer.Start();
    }

    // Typing settled: re-offer the name if the folder exists (single check).
    private void OfferTimer_Tick(object? sender, EventArgs e)
    {
        _offerTimer.Stop();
        if (Directory.Exists(NewFolderBox.Text.Trim().Trim('"')))
            OfferFreeName();
    }

    /// <summary>Offers the first free "New Project [N]" in the folder unless a custom name was typed.</summary>
    private void OfferFreeName()
    {
        if (NewNameBox.Text.Trim().Length > 0 && NewNameBox.Text != _offeredName)
            return;

        NewNameBox.Text = _offeredName = MainViewModel.FreeProjectName(NewFolderBox.Text);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder the project is saved in" };
        var folder = NewFolderBox.Text.Trim();
        if (Directory.Exists(folder))
            dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) != true)
            return;

        // Browsed folder exists: offer its free name immediately (no debounce).
        NewFolderBox.Text = dialog.FolderName;
        _offerTimer.Stop();
        OfferFreeName();
    }

    /// <summary>Create Project: stores the request (the main window creates it in a new tab) and closes.</summary>
    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (ChosenFrame() is not { } frame)
        {
            MessageText.Text = $"Type a width and a height of at least {NewProjectRequest.SmallestSide} pixels.";
            return;
        }

        var name = NewNameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageText.Text = "Type a name for the project.";
            NewNameBox.Focus();
            return;
        }

        var folder = NewFolderBox.Text.Trim().Trim('"');
        if (folder.Length > 0 && !Directory.Exists(folder))
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Controls.UiFault.Show(MessageText, $"The folder could not be made: {ex.Message}", "Project Manager: make a folder", ex);
                return;
            }
        }

        var style = PresetCards.SelectedItem is PresetCard { Path: { } path } ? path : null;
        NewProjectToCreate = new NewProjectRequest(name, folder.Length > 0 ? folder : null, frame.Width, frame.Height, ChosenRate(), ChosenColorSpace, style,
            [.. _picker?.SelectedFiles ?? []]);
        DialogResult = true;
    }

    // ----- Project Settings -----

    /// <summary>Project Settings rate option (null = first clip's rate).</summary>
    private sealed record RateChoice(Models.Timeline.FrameRate? Rate, string Label)
    {
        public override string ToString() => Label;
    }

    // Values FillSettings displayed; Apply sends frame and rate only when changed from these.
    private (string Width, string Height) _shownFrame = ("", "");
    private RateChoice? _shownRate;

    /// <summary>Loads current project settings (output frame including Export Resolution) and their source into the fields.</summary>
    private void FillSettings()
    {
        var current = _viewModel.CurrentProjectSettings;
        var document = _viewModel.Document;
        var hasClips = _viewModel.SequenceHasClips;
        SettingsSummary.Text = $"Now: {MainViewModel.Describe(current)}.";

        var sized = current.NaturalWidth > 0 && current.NaturalHeight > 0;
        var (width, height) = sized ? (_viewModel.FrameWidth, _viewModel.FrameHeight) : (0, 0);
        SettingsWidthBox.Text = width > 0 ? width.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        SettingsHeightBox.Text = height > 0 ? height.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        _shownFrame = (SettingsWidthBox.Text, SettingsHeightBox.Text);

        var rates = new List<RateChoice>();
        if (!hasClips)
            rates.Add(new RateChoice(null, "Auto from First Clip"));
        foreach (var rate in Models.Timeline.FrameRate.Standard.Append(current.FrameRate).Distinct())
            rates.Add(new RateChoice(rate, rate.Fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
        SettingsRateBox.ItemsSource = rates;
        SettingsRateBox.SelectedItem = _shownRate = !hasClips && !document.ExplicitFrameRate ? rates[0] : rates.First(r => r.Rate == current.FrameRate);
        SettingsRateBox.IsEnabled = !hasClips;

        LabeledChoice<Models.Timeline.ProjectColorSpace>[] colours =
            [.. Enum.GetValues<Models.Timeline.ProjectColorSpace>().Select(c => new LabeledChoice<Models.Timeline.ProjectColorSpace>(c, Models.Timeline.ProjectSettings.Label(c)))];
        SettingsColorBox.ItemsSource = colours;
        SettingsColorBox.SelectedItem = colours.First(c => c.Key == current.ColorSpace);

        LabeledChoice<Models.Timeline.ProjectPixelAspect>[] aspects =
            [.. Enum.GetValues<Models.Timeline.ProjectPixelAspect>().Select(a => new LabeledChoice<Models.Timeline.ProjectPixelAspect>(a, Models.Timeline.ProjectSettings.Label(a)))];
        SettingsAspectBox.ItemsSource = aspects;
        SettingsAspectBox.SelectedItem = aspects.First(a => a.Key == current.PixelAspect);

        LabeledChoice<int>[] sampleRates =
            [.. Models.Timeline.ProjectSettings.SampleRates.Select(r => new LabeledChoice<int>(r, $"{(r / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} kHz"))];
        SettingsSampleRateBox.ItemsSource = sampleRates;
        SettingsSampleRateBox.SelectedItem = sampleRates.FirstOrDefault(r => r.Key == current.SampleRate) ?? sampleRates[0];

        SettingsNote.Text = hasClips
            ? "The frame rate is fixed once the Sequence has clips: every edit lies on its frames. Start a New Project for another rate."
            : sized ? "" : "Leave the frame blank to take the first clip's size.";
        SourceRows.ItemsSource = _viewModel.SourceInfoRows;
    }

    /// <summary>
    /// Apply: sends frame/rate only if changed, so e.g. a sample-rate-only change neither resets Export Resolution nor marks them explicit.
    /// Clearing the frame or re-selecting Auto on an empty Sequence returns them to first-clip control.
    /// </summary>
    private void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        var (widthText, heightText) = (SettingsWidthBox.Text.Trim(), SettingsHeightBox.Text.Trim());
        var frameChanged = widthText != _shownFrame.Width.Trim() || heightText != _shownFrame.Height.Trim();
        var chosenRate = SettingsRateBox.SelectedItem as RateChoice;
        var rateChanged = chosenRate is not null && !Equals(chosenRate, _shownRate);
        int width = 0, height = 0;
        if (frameChanged && (widthText.Length > 0 || heightText.Length > 0))
        {
            if (!int.TryParse(widthText, out width) || !int.TryParse(heightText, out height)
                || width < NewProjectRequest.SmallestSide || height < NewProjectRequest.SmallestSide
                || width > NewProjectRequest.LargestSide || height > NewProjectRequest.LargestSide)
            {
                MessageText.Text = $"Type a width and a height from {NewProjectRequest.SmallestSide} to {NewProjectRequest.LargestSide} pixels, or leave both blank.";
                return;
            }

            // Round to even dimensions (encoder requirement).
            (width, height) = (width / 2 * 2, height / 2 * 2);
        }

        // Cleared or Auto: revert to first clip (empty Sequence only; the VM refuses otherwise).
        _viewModel.LeaveToFirstClip(frame: frameChanged && widthText.Length == 0 && heightText.Length == 0, rate: rateChanged && chosenRate?.Rate is null);

        var rate = rateChanged ? chosenRate?.Rate : null;
        var colour = SettingsColorBox.SelectedItem is LabeledChoice<Models.Timeline.ProjectColorSpace> c ? c.Key : Models.Timeline.ProjectColorSpace.Sdr709;
        var aspect = SettingsAspectBox.SelectedItem is LabeledChoice<Models.Timeline.ProjectPixelAspect> a ? a.Key : Models.Timeline.ProjectPixelAspect.Square;
        var sampleRate = SettingsSampleRateBox.SelectedItem is LabeledChoice<int> s ? s.Key : Models.Timeline.TimelineDocument.DefaultSampleRate;
        _viewModel.ApplyProjectSettings(width, height, rate, colour, aspect, sampleRate);
        FillSettings();
        MessageText.Text = _viewModel.StatusText;
    }

    // Latest listing generation; stale completions are dropped.
    private int _listing;

    private void RefreshList()
    {
        // External trigger (ProjectStore.Changed on import): marshal to the window's thread.
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(RefreshList);
            return;
        }

        _ = RefreshListAsync();
    }

    /// <summary>
    /// Lists saved projects. Each file is fully parsed, which blocked the UI thread for many frames, so parsing runs on the pool and
    /// the list fills when done.
    /// </summary>
    private async Task RefreshListAsync()
    {
        var listing = ++_listing;
        List<ProjectEntry> entries;
        try
        {
            entries = await Task.Run(() => ProjectStore.ListRecent());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entries = [];
        }

        if (listing != _listing || _closing.IsCancellationRequested)
            return;

        // With previews on, reserve each row's picture slot up front (dark until loaded or when none) so rows never resize.
        var previews = AppSettings.Current.ShowProjectPreviews ? Visibility.Visible : Visibility.Collapsed;
        var rows = entries.Select(e => new ProjectRow(e) { PictureVisibility = previews }).ToList();
        ProjectList.ItemsSource = rows;
        if (rows.Count == 0)
            MessageText.Text = "No projects saved yet.";
        if (AppSettings.Current.ShowProjectPreviews)
            _ = LoadPicturesAsync(rows);
    }

    // ----- The pictures -----

    /// <summary>
    /// Loads row pictures: baked thumbnail (decoded off the UI thread), else video poster; plus the scrub filmstrip from the Media
    /// Browser cache, or generated in one background job with the browser's strip settings so both share one strip per video.
    /// </summary>
    private async Task LoadPicturesAsync(IReadOnlyList<ProjectRow> rows)
    {
        var token = _closing.Token;
        var settings = AppSettings.Current;
        var (frames, frameHeight) = (Math.Clamp(settings.BrowserPreviewFrames, 2, 40), Math.Clamp(settings.BrowserPreviewHeight, 90, 1080));
        var missing = new List<ProjectRow>();
        try
        {
            // Decode all baked thumbnails first (already in the listing).
            foreach (var row in rows)
            {
                if (row.Entry.Thumbnail is not { Length: > 0 } jpeg)
                    continue;

                row.Baked = await Task.Run(() => ProjectThumbnail.Decode(jpeg, PictureHeight), token);
                if (row.Baked is { } baked && row.Picture is null)
                    (row.Poster, row.Picture, row.PictureVisibility) = (baked, baked, Visibility.Visible);
            }

            foreach (var row in rows)
            {
                var path = row.Entry.SourcePath;
                var kept = await Task.Run(() => FileState.Exists(path) ? MediaBrowserFrames.Kept(path, frames, frameHeight, PictureHeight, 0.3) : null, token);
                if (kept is { } found)
                    Show(row, found.Strip, (double)found.Width / Math.Max(found.Height, 1), found.Poster);
                else if (await Task.Run(() => FileState.Exists(path), token))
                    missing.Add(row);
            }

            if (missing.Count == 0)
                return;

            await WorkScheduler.Run(new WorkSpec("Project previews", WorkLane.Background) { Key = "project-previews" }, async work =>
            {
                foreach (var row in missing)
                {
                    work.Token.ThrowIfCancellationRequested();
                    var path = row.Entry.SourcePath;
                    if (await MediaBrowserFrames.DescribeAsync(path, work.Token) is not { Width: > 0, Height: > 0 } video)
                        continue;

                    var strip = await MediaBrowserFrames.StripAsync(path, video.Seconds, video.Width, video.Height, frames, frameHeight, work.Token);
                    if (strip is null)
                        continue;

                    var shape = (double)video.Width / video.Height;
                    var poster = await Task.Run(() => MediaBrowserFrames.Poster(strip, shape, PictureHeight, 0.3), work.Token);
                    await Dispatcher.InvokeAsync(() => Show(row, strip, shape, poster));
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            // Window closed: stop; remaining strips are built on a later open.
        }
    }

    private void Show(ProjectRow row, string strip, double shape, BitmapSource? poster)
    {
        // Keep the baked thumbnail as resting image; the strip is for scrubbing, the poster only for projects without a thumbnail.
        (row.Strip, row.Shape, row.Poster) = (strip, shape, row.Baked ?? poster);
        if (row.Poster is null || !AppSettings.Current.ShowProjectPreviews)
            return;

        (row.Picture, row.PictureVisibility) = (row.Poster, Visibility.Visible);
    }

    // Hover scrub: show the frame at the pointer position (strip decoded once on enter).
    private void Picture_MouseMove(object sender, MouseEventArgs e) => Faults.Forget(Picture_MouseMoveAsync(sender, e), nameof(Picture_MouseMove));

    private async Task Picture_MouseMoveAsync(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProjectRow row } picture || row.Strip is not { } strip)
            return;

        if (row.Frames is null)
        {
            if (row.DecodingFrames)
                return;

            row.DecodingFrames = true;
            var shape = row.Shape;
            row.Frames = await Task.Run(() => MediaBrowserFrames.Frames(strip, shape, PictureHeight));
            row.DecodingFrames = false;
            if (!picture.IsMouseOver)
            {
                row.Frames = null;
                return;
            }
        }

        if (row.Frames is { Count: > 0 } frames)
        {
            var at = Math.Clamp(e.GetPosition(picture).X / Math.Max(picture.ActualWidth, 1), 0, 0.999);
            row.Picture = frames[(int)(at * frames.Count)];
        }
    }

    private void Picture_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectRow row })
            (row.Picture, row.Frames) = (row.Poster, null);
    }

    private void PreviewsBox_Changed(object sender, RoutedEventArgs e)
    {
        var on = PreviewsBox.IsChecked == true;
        if (on == AppSettings.Current.ShowProjectPreviews)
            return;

        AppSettings.Update(settings => settings.ShowProjectPreviews = on);

        RefreshList();
    }

    // ----- Saving, starting anew, loading, deleting -----

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasSource)
        {
            MessageText.Text = "Load a video first: a project is a video with its cuts and settings.";
            return;
        }

        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageText.Text = "Type a name for the project.";
            return;
        }

        var saved = _viewModel.SaveProject(NameBox.Text);
        MessageText.Text = saved.Line;
        if (_closeOnSave && saved.IsSaved)
        {
            Close();
            return;
        }

        RefreshList();
    }

    /// <summary>Save as Bundle request: closes, and the main window shows Save As with the bundle type.</summary>
    public bool BundleRequested { get; private set; }

    private void SaveBundle_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasSource)
        {
            MessageText.Text = "Load a video first: a bundle is a project with the files it uses.";
            return;
        }

        BundleRequested = true;
        DialogResult = true;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a Project or Bundle",
            Filter = $"HandPeg projects and bundles|*{ProjectStore.Extension};*{ProjectBundle.Extension}|All files|*.*",
            InitialDirectory = Directory.Exists(ProjectStore.Folder) ? ProjectStore.Folder : "",
        };
        if (dialog.ShowDialog(this) != true || !ProjectStore.IsProjectFile(dialog.FileName))
            return;

        ProjectToLoad = dialog.FileName;
        DialogResult = true;
    }

    // + New Project switches to the New Project mode.
    private void NewProject_Click(object sender, RoutedEventArgs e) => ShowMode(ProjectManagerMode.NewProject);

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LoadButton.IsEnabled = DeleteButton.IsEnabled = ProjectList.SelectedItem is ProjectRow;

    // On the pool: creating the folder and launching Explorer cost tens of ms on the UI thread.
    private void OpenDirectory_Click(object sender, RoutedEventArgs e) => Faults.Forget(OpenDirectory_ClickAsync(sender, e), nameof(OpenDirectory_Click));

    private async Task OpenDirectory_ClickAsync(object sender, RoutedEventArgs e)
    {
        var folder = ProjectStore.Folder;
        var problem = await Task.Run<string?>(() =>
        {
            try
            {
                // The folder is created with the first saved project.
                Directory.CreateDirectory(folder);
                ShellOpen.OpenFolder(folder);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                AppLog.Write($"Project Manager: open the folder {folder}", ex);
                return ex.Message;
            }
        });

        if (problem is not null)
            MessageText.Text = $"Could not open the folder: {problem}";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectRow { Entry: var entry })
            return;

        var answer = MessageBox.Show(this,
            $"Delete the project \"{entry.Name}\"?\n\nThe project file is removed from disk. The video it refers to is not touched.",
            "HandPeg", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            File.Delete(entry.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Controls.UiFault.Show(MessageText, $"Could not delete the project: {ex.Message}", "Project Manager: delete a project", ex);
            return;
        }

        // Remove immediately; the pool refresh follows.
        var left = ProjectList.Items.OfType<ProjectRow>().Where(r => !ReferenceEquals(r.Entry, entry)).ToList();
        ProjectList.ItemsSource = left;
        ProjectList.SelectedItem = null;
        RefreshList();
        MessageText.Text = left.Count == 0 ? "No projects saved yet." : $"Deleted project \"{entry.Name}\".";
    }

    private void ProjectList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LoadSelected();

    private void Load_Click(object sender, RoutedEventArgs e) => LoadSelected();

    private void LoadSelected()
    {
        if (ProjectList.SelectedItem is not ProjectRow { Entry: var entry })
            return;

        // During an encode, queue the project as a new job instead of loading it.
        ProjectToLoad = entry.FilePath;
        DialogResult = true;
    }
}
