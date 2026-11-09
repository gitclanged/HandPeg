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
/// A saved project as the Project Manager lists it (4.1.1-B): its entry, and its picture (its baked thumbnail since Pass
/// 5.2D, else its video's poster), which the pointer scrubs through the video's frames while it is across it.
/// </summary>
public sealed partial class ProjectRow(ProjectEntry entry) : ObservableObject
{
    public ProjectEntry Entry { get; } = entry;

    /// <summary>The project's name: what the row is called to screen readers and UI Automation (4.2B-3), not the type's name.</summary>
    public string Name => Entry.Name;

    public override string ToString() => Name;

    /// <summary>What the picture shows: the poster, or while the pointer is across it, the frame there.</summary>
    [ObservableProperty] private ImageSource? _picture;

    [ObservableProperty] private Visibility _pictureVisibility = Visibility.Collapsed;

    /// <summary>What the picture shows when the pointer is not across it: the project's baked thumbnail when it has one, else its video's poster.</summary>
    public BitmapSource? Poster { get; set; }

    /// <summary>The project's own thumbnail (Pass 5.2D: the Sequence as it was saved), decoded; null when its file has none.</summary>
    public BitmapSource? Baked { get; set; }

    /// <summary>The media browser's strip of the main video's frames (its cache's file), and the video's shape.</summary>
    public string? Strip { get; set; }

    public double Shape { get; set; } = 16.0 / 9;

    /// <summary>The strip's frames, decoded while the pointer is across the picture and let go when it leaves.</summary>
    public IReadOnlyList<BitmapSource>? Frames { get; set; }

    public bool DecodingFrames { get; set; }
}

/// <summary>The Project Manager's three modes (5.4A).</summary>
public enum ProjectManagerMode
{
    /// <summary>New Project: a project's name and folder, frame, rate, colour space, Game Preset and media.</summary>
    NewProject,

    /// <summary>Open / Manage Projects: save the open project, open a saved one, delete one (the window before 5.4A).</summary>
    Manage,

    /// <summary>Project Settings: the open project's frame, rate, colour space, pixel aspect and sample rate.</summary>
    Settings,
}

/// <summary>A Game Preset card of the New Project studio: its name, its file (null for none) and what its tooltip says.</summary>
public sealed record PresetCard(string Name, string? Path, string About)
{
    public override string ToString() => Name;
}

/// <summary>
/// The Project Manager (5.4A, a studio of three modes): New Project makes a project (a request the main window carries out: the dialog
/// opens nothing itself), Open / Manage Projects saves the current session as a project and lists the saved ones (since 4.1.1-B with a
/// 16:9 picture of each project's main video, the media browser's poster and strip, from its cache, made in the background when not
/// kept, scrubbed by the pointer, while Settings' Show video &amp; hover previews in Project Manager is on), and Project Settings shows and
/// sets the open project's settings.
/// </summary>
public partial class ProjectManagerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly CancellationTokenSource _closing = new();

    // How tall the pictures are decoded: the list's picture is 72 pixels tall, at up to twice that on a high-DPI screen.
    private const int PictureHeight = 144;

    // Set once the window's parts are all made: the format's handlers run while the XAML is read, before the later parts exist.
    private readonly bool _ready;

    /// <param name="focusName">Opened to save (Ctrl+S on a new project, Save As): the name box has the keyboard, its text chosen (Open / Manage).</param>
    /// <param name="mode">The mode it opens in (Open / Manage when opened to save).</param>
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

        // New Project starts as Settings says every video starts: tall or wide, in the Projects folder, under the first name free there.
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

        // Projects imported while the list is showing appear in it at once.
        ProjectStore.Changed += RefreshList;
        Closed += (_, _) =>
        {
            ProjectStore.Changed -= RefreshList;
            _offerTimer.Stop();
            _closing.Cancel();

            // The picker gives back its timers, its listing and its pictures (up to 32 MB of them) with the dialog.
            if (_picker is { } picker)
            {
                picker.SelectedFilesChanged -= UpdatePicked;
                picker.Release();
                PickerHost.Child = null;
                _picker = null;
            }
        };
    }

    /// <summary>The project the user chose to open, for the main window to load once this one has closed.</summary>
    public string? ProjectToLoad { get; private set; }

    // Opened to save: a project saved is what it was opened for.
    private readonly bool _closeOnSave;

    /// <summary>
    /// A blank new project was asked for: the main window empties the session (asking about unsaved changes first). Kept for the main
    /// window's handling; since 5.4A + New Project shows the New Project mode, whose request is <see cref="NewProjectToCreate"/>.
    /// </summary>
    public bool NewProjectRequested { get; private set; }

    /// <summary>The project New Project's Create Project asked for (5.4A), for the main window to make once this one has closed; null for none.</summary>
    public NewProjectRequest? NewProjectToCreate { get; private set; }

    // ----- The modes -----

    // The mode shown; null before the first. Whether the Game Preset cards were asked for (New Project shown once).
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

    /// <summary>Shows a mode: its part of the window, its button chosen; New Project makes its picker and cards the first time.</summary>
    private void ShowMode(ProjectManagerMode mode)
    {
        // Choosing the mode's button below comes back here (Mode_Checked): the mode is shown once.
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

    // The Media Browser as the picker (MediaBrowserPane.PickerMode), made when New Project is first shown.
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

    /// <summary>The Game Preset cards: None first, then the Styles folder's presets (read off the interface), Overwatch 2 first when it is there.</summary>
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

    /// <summary>A Game Preset chosen: the frame turns tall or wide as the preset's layout is (the aspect can still be chosen after).</summary>
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

    // The aspect, resolution, custom size, rate or colour space changed: the summary under them, and whether Create Project can be pressed.
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

    /// <summary>The frame chosen: the aspect at the resolution's short side, or the custom size; null for a custom size not yet a size.</summary>
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

    /// <summary>The rate chosen; null for Auto from First Clip.</summary>
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

    // The name New Project offered last (5.4A): offered again for the folder chosen while the name box still holds it (or nothing).
    private string _offeredName = "";

    // The location box offers the free name again once typing rests (5.4A): each keystroke would otherwise look through the folder
    // typed so far, a share's half-typed path among them, on the UI thread.
    private readonly System.Windows.Threading.DispatcherTimer _offerTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private void NewFolder_TextChanged(object sender, TextChangedEventArgs e)
    {
        _offerTimer.Stop();
        _offerTimer.Start();
    }

    // Typing has rested: the name offered again, only for a folder that is there (looked at once).
    private void OfferTimer_Tick(object? sender, EventArgs e)
    {
        _offerTimer.Stop();
        if (Directory.Exists(NewFolderBox.Text.Trim().Trim('"')))
            OfferFreeName();
    }

    /// <summary>Offers the first of "New Project", "New Project 2"... that no project in the chosen folder has, unless a name of the user's own is typed.</summary>
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

        // A folder chosen is there: its free name at once, not after the location box's pause.
        NewFolderBox.Text = dialog.FolderName;
        _offerTimer.Stop();
        OfferFreeName();
    }

    /// <summary>Create Project: the request handed to the main window (which makes the project in a tab of its own), and the dialog closed.</summary>
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
                MessageText.Text = $"The folder could not be made: {ex.Message}";
                return;
            }
        }

        var style = PresetCards.SelectedItem is PresetCard { Path: { } path } ? path : null;
        NewProjectToCreate = new NewProjectRequest(name, folder.Length > 0 ? folder : null, frame.Width, frame.Height, ChosenRate(), ChosenColorSpace, style,
            [.. _picker?.SelectedFiles ?? []]);
        DialogResult = true;
    }

    // ----- Project Settings -----

    /// <summary>A frame rate as the Project Settings list offers it (null: the first clip's own).</summary>
    private sealed record RateChoice(Models.Timeline.FrameRate? Rate, string Label)
    {
        public override string ToString() => Label;
    }

    // What FillSettings showed in the frame boxes and the rate list (5.4A): Apply sends the frame and the rate only when they were changed.
    private (string Width, string Height) _shownFrame = ("", "");
    private RateChoice? _shownRate;

    /// <summary>Shows the open project's settings in the boxes (the frame as it is output, Export's Resolution boxes and all), and what its source is.</summary>
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
    /// Apply: the frame and the rate go to the project only when they were changed from what was shown (5.4A), so a change of the sample
    /// rate alone neither resets Export's Resolution nor marks the frame and rate as chosen. The frame emptied, or Auto from First Clip
    /// chosen again, on an empty Sequence leaves them to the first clip once more.
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

            // Even, as every frame the encoders take is.
            (width, height) = (width / 2 * 2, height / 2 * 2);
        }

        // Emptied, or Auto again: the first clip's (an empty Sequence only; the view model refuses it otherwise).
        _viewModel.LeaveToFirstClip(frame: frameChanged && widthText.Length == 0 && heightText.Length == 0, rate: rateChanged && chosenRate?.Rate is null);

        var rate = rateChanged ? chosenRate?.Rate : null;
        var colour = SettingsColorBox.SelectedItem is LabeledChoice<Models.Timeline.ProjectColorSpace> c ? c.Key : Models.Timeline.ProjectColorSpace.Sdr709;
        var aspect = SettingsAspectBox.SelectedItem is LabeledChoice<Models.Timeline.ProjectPixelAspect> a ? a.Key : Models.Timeline.ProjectPixelAspect.Square;
        var sampleRate = SettingsSampleRateBox.SelectedItem is LabeledChoice<int> s ? s.Key : Models.Timeline.TimelineDocument.DefaultSampleRate;
        _viewModel.ApplyProjectSettings(width, height, rate, colour, aspect, sampleRate);
        FillSettings();
        MessageText.Text = _viewModel.StatusText;
    }

    // The listing asked for last: one that finishes after a newer one was asked for is dropped.
    private int _listing;

    private void RefreshList()
    {
        // Asked from elsewhere (projects imported: ProjectStore.Changed): listed on the window's own thread.
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(RefreshList);
            return;
        }

        _ = RefreshListAsync();
    }

    /// <summary>
    /// Lists the saved projects. Each project file is read whole to say what it holds, which for a folder of them took
    /// the interface's thread far past a frame as the window opened (4.3E): they are read on the thread pool, and the list
    /// is filled once they are.
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

        // With previews on, every row has its picture's place from the start (dark until its picture comes, and dark for a
        // source that has none, such as a link): the rows are one height and do not grow as their pictures arrive.
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
    /// Each project's picture: its baked thumbnail when its file carries one (Pass 5.2D: the Sequence as it was
    /// saved, decoded off the interface), else its video's poster. The video's strip, which the pointer scrubs, from the media browser's
    /// cache when it is kept there (read off the interface), otherwise made in the background (one piece of work for them all), at the
    /// browser's own strip settings so that the browser and this list share one strip per video.
    /// </summary>
    private async Task LoadPicturesAsync(IReadOnlyList<ProjectRow> rows)
    {
        var token = _closing.Token;
        var settings = AppSettings.Current;
        var (frames, frameHeight) = (Math.Clamp(settings.BrowserPreviewFrames, 2, 40), Math.Clamp(settings.BrowserPreviewHeight, 90, 1080));
        var missing = new List<ProjectRow>();
        try
        {
            // The baked thumbnails first, all of them: they are in the listing already, and each takes a moment to decode.
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
            // The window was closed: the rest are made another time.
        }
    }

    private void Show(ProjectRow row, string strip, double shape, BitmapSource? poster)
    {
        // The baked thumbnail stays the picture: the video's strip is for the pointer's scrub, and its poster for a project without one.
        (row.Strip, row.Shape, row.Poster) = (strip, shape, row.Baked ?? poster);
        if (row.Poster is null || !AppSettings.Current.ShowProjectPreviews)
            return;

        (row.Picture, row.PictureVisibility) = (row.Poster, Visibility.Visible);
    }

    // The pointer across a picture: the frame at that point of the video (the strip decoded once, on the way in).
    private async void Picture_MouseMove(object sender, MouseEventArgs e)
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

        AppSettings.Current.ShowProjectPreviews = on;
        try
        {
            AppSettings.Current.SaveAsCurrent();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not written: the choice holds for this session.
        }

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

        MessageText.Text = _viewModel.SaveProject(NameBox.Text);
        if (_closeOnSave && MessageText.Text.StartsWith("Project saved", StringComparison.Ordinal))
        {
            Close();
            return;
        }

        RefreshList();
    }

    /// <summary>Asked for by Save as Bundle: the window closes and the main window asks where (Save As with the bundle type).</summary>
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

    // + New Project (5.4A): the New Project mode, where the project is made.
    private void NewProject_Click(object sender, RoutedEventArgs e) => ShowMode(ProjectManagerMode.NewProject);

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LoadButton.IsEnabled = DeleteButton.IsEnabled = ProjectList.SelectedItem is ProjectRow;

    // On the thread pool (4.3E): making the folder and starting Explorer took the interface's thread for tens of milliseconds.
    private async void OpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        var folder = ProjectStore.Folder;
        var problem = await Task.Run<string?>(() =>
        {
            try
            {
                // The folder only comes into being with the first saved project.
                Directory.CreateDirectory(folder);
                System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"")?.Dispose();
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
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
            MessageText.Text = $"Could not delete the project: {ex.Message}";
            return;
        }

        // Gone from the list at once (the list read afresh comes a moment later, from the thread pool).
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

        // While something is encoding, the project is not loaded into the window: it goes into the
        // queue as a new job instead, without disturbing what is running.
        ProjectToLoad = entry.FilePath;
        DialogResult = true;
    }
}
