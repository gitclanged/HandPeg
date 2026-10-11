using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HandPegApp.Services;
using Microsoft.Win32;

namespace HandPegApp;

/// <summary>What the launch window was asked to open.</summary>
public enum LaunchKind
{
    Video,
    Project,
}

/// <param name="Path">The video, or the project file.</param>
/// <param name="StylePath">The style preset to give the video, or null to open it as it is.</param>
public sealed record LaunchRequest(LaunchKind Kind, string Path, string? StylePath = null);

/// <summary>A style preset as the launch window and the settings list it: a file in the Styles folder.</summary>
public sealed record StyleFile(string Path)
{
    public const string Extension = ".hpstyle";

    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The style presets in the Styles folder, most recently saved first.</summary>
    public static List<StyleFile> All()
    {
        try
        {
            return Directory.Exists(AppPaths.Styles)
                ? new DirectoryInfo(AppPaths.Styles).EnumerateFiles("*" + Extension).OrderByDescending(f => f.LastWriteTime).Select(f => new StyleFile(f.FullName)).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The ones the launch window shows: those picked in the settings, or failing that the most recent, up to the number set.</summary>
    public static List<StyleFile> ForLaunch()
    {
        var (all, settings) = (All(), AppSettings.Current);
        var picked = all.Where(s => settings.SplashStylePresets.Contains(s.FileName, StringComparer.OrdinalIgnoreCase)).ToList();
        return (picked.Count > 0 ? picked : all).Take(Math.Clamp(settings.SplashPresetCount, 0, 5)).ToList();
    }
}

/// <summary>
/// The launch window, shown over the main window as it starts in Editor Mode, when it is switched on in the
/// settings: a place to drop a video, a way back into a recent project, and style presets as drop targets.
/// It only records what was asked for; the main window, which owns it, does the opening once it has closed.
/// </summary>
public partial class SplashWindow : Window
{
    private const string VideoFilter = "Media files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.m2ts;*.wmv;*.flv;*.mpg;*.mpeg;*.mp3;*.m4a;*.flac;*.wav|All files|*.*";

    public SplashWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var styles = StyleFile.ForLaunch();
        StyleList.ItemsSource = styles;
        (StylePanel.Visibility, NoStylesText.Visibility) = styles.Count > 0 ? (Visibility.Visible, Visibility.Collapsed) : (Visibility.Collapsed, Visibility.Visible);

        var squish = AppSettings.Current.ShowSquisher
            ? AppSettings.Current.SquisherPresets.Where(p => p.TargetSizeMb > 0).Take(Math.Clamp(AppSettings.Current.SquisherPresetCount, 1, 12)).ToList()
            : [];
        SquishList.ItemsSource = squish;
        SquisherPanel.Visibility = squish.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Closed += (_, _) => EndSquish();

        var recent = ProjectStore.ListRecent().Take(12).ToList();
        RecentBox.ItemsSource = recent;
        RecentBox.SelectedIndex = recent.Count > 0 ? 0 : -1;
        RecentButton.IsEnabled = RecentBox.IsEnabled = recent.Count > 0;
        if (recent.Count == 0)
            RecentButton.ToolTip = "There are no saved projects yet. Save one with the Project button in the main window.";
    }

    /// <summary>What to open once this window has closed; null for nothing (a blank project, or the window was simply closed).</summary>
    public LaunchRequest? Request { get; private set; }

    private void Finish(LaunchRequest? request)
    {
        Request = request;
        Close();
    }

    private void NewBlank_Click(object sender, RoutedEventArgs e) => Finish(null);

    private void LoadRecent_Click(object sender, RoutedEventArgs e)
    {
        if (RecentBox.SelectedItem is ProjectEntry project)
            Finish(new LaunchRequest(LaunchKind.Project, project.FilePath));
    }

    // ----- The Social Sharing Squisher -----
    // A video dropped on a preset is not opened: it is squeezed under the preset's size, here. The window
    // shrinks to a square that shows one still frame of the video with blocks crunching over it and how far
    // along the encode is, and when it is done the picture is the file: dragged out of the window, it is
    // dropped wherever a file can be (Discord, a browser, a folder). The file is a temporary one, and goes
    // when the window closes.

    private static readonly string[] SquishTexts =
    [
        "Destroying video quality...", "Taking out pixels...", "Generating banding...", "Rounding off the details...",
        "Asking the encoder nicely...", "Deleting every other pixel...", "Smearing the gradients...", "Counting bits, twice...",
        "Making it worse, on purpose...", "Squeezing harder...", "Flattening the dark bits...", "Negotiating with the bitrate...",
        "Converting 4K to potato...", "Deleting every 3rd pixel...", "Bribing the encoder...", "Downloading more RAM...",
        "Applying cinematic blockiness...", "Making it fit on a floppy...", "Reticulating splines...", "Optimizing for dial-up...",
        "Starving the bitrate...", "Folding the video in half...", "Sanding off the sharp edges...", "Teaching pixels to share...",
        "Replacing detail with vibes...", "Compressing the compression...", "Laundering the color depth...", "Turning gradients into stairs...",
        "Evicting high frequencies...", "Feeding frames to the shredder...", "Consulting the macroblock oracle...", "Putting the video on a diet...",
        "Rounding 60 fps down to enough...", "Hiding the artifacts in the shadows...", "Convincing it that 480p is plenty...", "Wringing out the last kilobits...",
    ];

    /// <summary>Starts squishing a video for a preset, as dropping it on the preset in this window would: for a drop made elsewhere (the main window's overlay).</summary>
    /// <param name="instant">The window goes to its processing square at once, with no shrinking shown: for a drop on the overlay, which opened it only for this.</param>
    public void StartSquish(string file, SquishPreset preset, bool instant = false) => _ = SquishAsync(file, preset, instant);

    public static readonly DependencyProperty ShrinkProperty = DependencyProperty.Register(
        nameof(Shrink), typeof(double), typeof(SplashWindow), new PropertyMetadata(0.0, (d, _) => ((SplashWindow)d).ApplyShrink()));

    /// <summary>How far the window has shrunk to the processing square: 0 as it was, 1 the square. Animated; the window's size and place follow it.</summary>
    public double Shrink
    {
        get => (double)GetValue(ShrinkProperty);
        set => SetValue(ShrinkProperty, value);
    }

    /// <summary>The hardware encoders this computer has, asked for when a squish starts: they are still being found when the window opens.</summary>
    public Func<IReadOnlyCollection<string>>? AvailableEncoders { get; set; }

    /// <summary>A squish was started in this window: it is no longer put away by a click elsewhere, since what it made would go with it.</summary>
    public bool IsSquishing { get; private set; }

    private const double SquareWidth = 380, SquareHeight = 430;
    private (double Left, double Top, double Width, double Height) _fullSize;
    private CancellationTokenSource? _squish;
    private string _squishedFile = "";
    private string _squishFolder = "";
    private readonly System.Windows.Threading.DispatcherTimer _squishTextTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    private void ApplyShrink()
    {
        var (t, full) = (Shrink, _fullSize);
        (Width, Height) = (full.Width + (SquareWidth - full.Width) * t, full.Height + (SquareHeight - full.Height) * t);

        // About its middle: the square ends up where the middle of the window was.
        (Left, Top) = (full.Left + (full.Width - Width) / 2, full.Top + (full.Height - Height) / 2);
    }

    private void Squish_Drop(object sender, DragEventArgs e)
    {
        if (sender is UIElement target)
            target.Opacity = 1;
        e.Handled = true;
        if (DroppedFile(e) is { } file && sender is FrameworkElement { DataContext: SquishPreset preset })
            _ = SquishAsync(file, preset);
    }

    private void Squish_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SquishPreset preset } && Browse() is { } file)
            _ = SquishAsync(file, preset);
    }

    private async Task SquishAsync(string file, SquishPreset preset, bool instant = false)
    {
        if (IsSquishing)
            return;

        IsSquishing = true;
        var cancel = _squish = new CancellationTokenSource();
        var settings = AppSettings.Current;
        _squishFolder = Path.Combine(SessionPaths.Root, "squish_" + Guid.NewGuid().ToString("N")[..8]);

        // The window as it is, and then down to the square.
        _fullSize = (Left, Top, ActualWidth, ActualHeight);
        SizeToContent = SizeToContent.Manual;
        (MainPanel.Visibility, SquishPanel.Visibility) = (Visibility.Collapsed, Visibility.Visible);
        Title = $"Squishing for {preset.Name}";
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
        if (instant)
        {
            Shrink = 1;
            Opacity = 1;
        }
        else
        {
            BeginAnimation(ShrinkProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        }

        var random = new Random();
        void NextText(object? sender, EventArgs e) => SquishText.Text = SquishTexts[random.Next(SquishTexts.Length)];
        _squishTextTimer.Tick += NextText;
        _squishTextTimer.Start();
        SquishDetail.Text = $"{Path.GetFileName(file)}  \u2192  {preset.SizeText}";

        try
        {
            // One still frame, and no player: it comes to the middle as it arrives, and the blocks start on it.
            SquishPicture.Thumbnail = await SocialSquisher.ExtractThumbnailAsync(file, Path.Combine(_squishFolder, "thumbnail.jpg"), cancel.Token);
            SquishPicture.IsCrunching = true;
            var arrive = new System.Windows.Media.Animation.DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease };
            SquishPictureScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, arrive);
            SquishPictureScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, arrive);
            SquishPicture.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));

            var slug = string.Concat(preset.Name.Where(char.IsLetterOrDigit));
            var output = Path.Combine(_squishFolder, $"{Path.GetFileNameWithoutExtension(file)}_{(slug.Length > 0 ? slug : "squished")}.mp4");
            var result = await SocialSquisher.SquishAsync(
                file, preset, settings.SquisherOffsetMb, AvailableEncoders?.Invoke() ?? [],
                settings.SquisherManualPreset ? settings.SquisherEncoderPreset : null, output,
                new Progress<double>(value => SquishProgress.Value = value), cancel.Token);

            _squishedFile = result.Path;
            SquishPicture.IsCrunching = false;
            SquishPicture.Cursor = Cursors.Hand;
            SquishPicture.ToolTip = "Drag the file out of the window to drop the squished video somewhere: Discord, a browser, a folder.";
            SquishProgress.Value = 1;
            SquishText.Text = "Squished. Drag the file out to share it.";
            SquishDetail.Text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{result.Bytes / 1048576.0:0.0} MB of {preset.TargetSizeMb:0.#} MB  \u00B7  {result.Encoder}, {result.VideoKbps} kb/s{(result.Attempts > 1 ? $", {result.Attempts} tries" : "")}");
            SquishCloseButton.Content = "Close";
            SquishFinishButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            // Closed while it was running.
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SquishPicture.IsCrunching = false;
            SquishText.Text = "It would not squish.";
            SquishDetail.Text = ex.Message;
            SquishCloseButton.Content = "Close";
            SquishFinishButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _squishTextTimer.Stop();
            _squishTextTimer.Tick -= NextText;
        }
    }

    // The finished picture is the file: pressed and dragged, it is carried out of the window as a file is from Explorer.
    private void SquishPicture_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_squishedFile.Length == 0 || !File.Exists(_squishedFile))
            return;

        var data = new DataObject(DataFormats.FileDrop, new[] { _squishedFile });
        DragDrop.DoDragDrop(SquishPicture, data, DragDropEffects.Copy);
        e.Handled = true;
    }

    private void SquishClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Finish: what was squished is cleared away (its file with it), and the window opens out again to the
    /// screen it started on, ready for another video.
    /// </summary>
    private void SquishFinish_Click(object sender, RoutedEventArgs e)
    {
        EndSquish();
        (_squishedFile, _squishFolder, _squish, IsSquishing) = ("", "", null, false);

        SquishPicture.Thumbnail = null;
        SquishPicture.Cursor = null;
        SquishPicture.ToolTip = null;
        SquishPicture.BeginAnimation(OpacityProperty, null);
        SquishPicture.Opacity = 0;
        (SquishProgress.Value, SquishText.Text, SquishDetail.Text) = (0, "Warming up the squisher...", "");
        (SquishCloseButton.Content, SquishFinishButton.Visibility) = ("Cancel", Visibility.Collapsed);
        Title = "HandPeg";

        (MainPanel.Visibility, SquishPanel.Visibility) = (Visibility.Visible, Visibility.Collapsed);
        var open = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
        };
        open.Completed += (_, _) =>
        {
            // The window is its content's height again, as it was before it shrank.
            BeginAnimation(ShrinkProperty, null);
            Shrink = 0;
            SizeToContent = SizeToContent.Height;
        };
        BeginAnimation(ShrinkProperty, open);
    }

    /// <summary>Stops a squish that is still running, and takes away what it made: the file was only ever a temporary one.</summary>
    private void EndSquish()
    {
        _squish?.Cancel();
        _squishTextTimer.Stop();
        SquishPicture.IsCrunching = false;
        var folder = _squishFolder;
        if (folder.Length == 0)
            return;

        // A moment later, and off this thread: FFmpeg, just told to stop, may still be holding the file.
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(folder))
                        Directory.Delete(folder, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(300);
                }
            }
        });
    }

    // ----- Dropping -----

    private static string? DroppedFile(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0]) ? files[0] : null;

    private void DropTarget_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // The target under the pointer lights up, so it is clear which style a drop would use.
    private void DropTarget_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is UIElement target && e.Data.GetDataPresent(DataFormats.FileDrop))
            target.Opacity = 0.65;
    }

    private void DropTarget_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is UIElement target)
            target.Opacity = 1;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone.Opacity = 1;
        e.Handled = true;
        if (DroppedFile(e) is { } file)
            Finish(new LaunchRequest(LaunchKind.Video, file));
    }

    private void DropZone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Browse() is { } file)
            Finish(new LaunchRequest(LaunchKind.Video, file));
    }

    private void Style_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: StyleFile style } button)
            return;

        button.Opacity = 1;
        if (DroppedFile(e) is { } file)
            Finish(new LaunchRequest(LaunchKind.Video, file, style.Path));
    }

    private void Style_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: StyleFile style } && Browse() is { } file)
            Finish(new LaunchRequest(LaunchKind.Video, file, style.Path));
    }

    private string? Browse()
    {
        var dialog = new OpenFileDialog { Title = "Select a video", Filter = VideoFilter };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}
