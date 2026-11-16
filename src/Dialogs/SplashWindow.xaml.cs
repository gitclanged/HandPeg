using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using HandPegApp.Services;
using Microsoft.Win32;

namespace HandPegApp;

/// <summary>Open request recorded by the launch window.</summary>
public enum LaunchKind
{
    Video,
    Project,
}

/// <param name="Path">Video or project file.</param>
/// <param name="StylePath">Game Preset to apply to the video, or null to open it unstyled.</param>
public sealed record LaunchRequest(LaunchKind Kind, string Path, string? StylePath = null);

/// <summary>Recent-projects row: entry plus its baked thumbnail decoded at small size.</summary>
/// <param name="Thumbnail">Thumbnail; null when the project file has none.</param>
/// <param name="ThumbnailVisibility">Visible, Hidden (reserve space when another row has one) or Collapsed (none do).</param>
public sealed record RecentProject(ProjectEntry Entry, BitmapSource? Thumbnail, Visibility ThumbnailVisibility)
{
    public string Name => Entry.Name;

    public string SavedText => Entry.SavedText;

    /// <summary>Project name for keyboard text search and screen readers.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Optional launch window over the starting main window: video drop zone, recent projects and Game Preset drop targets. It only
/// records the request; the owning main window performs the open after it closes.
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

        // Parse recent project files off the UI thread; the list populates shortly after.
        RecentButton.IsEnabled = RecentBox.IsEnabled = false;
        _ = ShowRecentAsync();
    }

    // Thumbnail decode height: shown at 40 DIP, up to 2x on high-DPI displays.
    private const int ThumbnailDecodeHeight = 80;

    private async Task ShowRecentAsync()
    {
        List<RecentProject> recent;
        try
        {
            recent = await Task.Run(() =>
            {
                var entries = ProjectStore.ListRecent().Take(12).ToList();

                // Decode baked thumbnails off the UI thread; rows without one reserve the space when others have one so names align.
                var anyThumbnail = entries.Exists(e => e.Thumbnail is { Length: > 0 });
                return entries.Select(e =>
                {
                    var thumbnail = e.Thumbnail is { Length: > 0 } jpeg ? ProjectThumbnail.Decode(jpeg, ThumbnailDecodeHeight) : null;
                    return new RecentProject(e, thumbnail, thumbnail is not null ? Visibility.Visible : anyThumbnail ? Visibility.Hidden : Visibility.Collapsed);
                }).ToList();
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            recent = [];
        }

        RecentBox.ItemsSource = recent;
        RecentBox.SelectedIndex = recent.Count > 0 ? 0 : -1;
        RecentButton.IsEnabled = RecentBox.IsEnabled = recent.Count > 0;
        if (recent.Count == 0)
            RecentButton.ToolTip = "There are no saved projects yet. Save one with the Project button in the main window.";
    }

    /// <summary>Request to open after close; null for none (blank project or dismissed).</summary>
    public LaunchRequest? Request { get; private set; }

    private void Finish(LaunchRequest? request)
    {
        Request = request;
        Close();
    }

    private void NewBlank_Click(object sender, RoutedEventArgs e) => Finish(null);

    private void LoadRecent_Click(object sender, RoutedEventArgs e)
    {
        if (RecentBox.SelectedItem is RecentProject { Entry: var project })
            Finish(new LaunchRequest(LaunchKind.Project, project.FilePath));
    }

    // ----- Social Sharing Squisher -----
    // A video dropped on a preset is encoded under the preset's size limit instead of opened. The window shrinks to a square showing
    // a still frame with crunching blocks and encode progress; when done, the picture is draggable as the output file (to chat apps,
    // browsers, folders). The temporary file is deleted when the window closes.

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

    /// <summary>Starts a squish as a preset drop would, for drops made elsewhere (main window overlay).</summary>
    /// <param name="instant">Jump straight to the processing square with no shrink animation (overlay-opened window).</param>
    public void StartSquish(string file, SquishPreset preset, bool instant = false) => _ = SquishAsync(file, preset, instant);

    public static readonly DependencyProperty ShrinkProperty = DependencyProperty.Register(
        nameof(Shrink), typeof(double), typeof(SplashWindow), new PropertyMetadata(0.0, (d, _) => ((SplashWindow)d).ApplyShrink()));

    /// <summary>Animated shrink progress (0 = original, 1 = processing square); window size and position follow it.</summary>
    public double Shrink
    {
        get => (double)GetValue(ShrinkProperty);
        set => SetValue(ShrinkProperty, value);
    }

    /// <summary>Available hardware encoders, resolved at squish start (enumeration may still run when the window opens).</summary>
    public Func<IReadOnlyCollection<string>>? AvailableEncoders { get; set; }

    /// <summary>A squish started here: deactivation no longer closes the window (that would delete its output).</summary>
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

        // Shrink about the centre so the square ends where the window's centre was.
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

        // Animate from the current bounds to the square.
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
            // Single still frame (no player), centred on arrival; the block effect starts on it.
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
            // Window closed mid-run.
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SquishPicture.IsCrunching = false;
            SquishText.Text = "It would not squish.";
            Controls.UiFault.Show(SquishDetail, ex.Message, "Squisher", ex);
            SquishCloseButton.Content = "Close";
            SquishFinishButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _squishTextTimer.Stop();
            _squishTextTimer.Tick -= NextText;
        }
    }

    // Dragging the finished picture starts a file drag-drop (as from Explorer).
    private void SquishPicture_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_squishedFile.Length == 0 || !File.Exists(_squishedFile))
            return;

        var data = new DataObject(DataFormats.FileDrop, new[] { _squishedFile });
        DragDrop.DoDragDrop(SquishPicture, data, DragDropEffects.Copy);
        e.Handled = true;
    }

    private void SquishClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Finish: deletes the squished output and restores the window to its original screen, ready for another video.</summary>
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
            // Restore SizeToContent height as before the shrink.
            BeginAnimation(ShrinkProperty, null);
            Shrink = 0;
            SizeToContent = SizeToContent.Height;
        };
        BeginAnimation(ShrinkProperty, open);
    }

    /// <summary>Cancels a running squish and deletes its temporary output.</summary>
    private void EndSquish()
    {
        _squish?.Cancel();
        _squishTextTimer.Stop();
        SquishPicture.IsCrunching = false;
        var folder = _squishFolder;
        if (folder.Length == 0)
            return;

        // Delete later off-thread: FFmpeg may still hold the file briefly after being stopped.
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

    // Highlight the drop target under the pointer to show which preset applies.
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
