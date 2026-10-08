using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HandPegApp.Models;
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
/// <param name="PresetName">The preset to apply to the video, or null to open it the usual way (smart rules and all).</param>
/// <param name="Parts">Which of that preset's settings to bring in.</param>
public sealed record LaunchRequest(LaunchKind Kind, string Path, string? PresetName = null, PresetParts Parts = PresetParts.All);

/// <summary>
/// The launch window, shown before the main window when it is switched on in the settings: a place to drop a
/// video, a way back into a recent project, and the first few presets as drop targets. It only records what
/// was asked for; the main window, once it is up, does the opening.
/// </summary>
public partial class SplashWindow : Window
{
    private const string VideoFilter = "Media files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.m2ts;*.wmv;*.flv;*.mpg;*.mpeg;*.mp3;*.m4a;*.flac;*.wav|All files|*.*";

    public SplashWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var presets = PresetStore.Load().Take(Math.Clamp(AppSettings.Current.SplashPresetCount, 0, 5)).ToList();
        PresetList.ItemsSource = presets;
        if (presets.Count == 0)
            PresetPanel.Visibility = Visibility.Collapsed;

        var recent = ProjectStore.ListRecent().Take(12).ToList();
        RecentBox.ItemsSource = recent;
        RecentBox.SelectedIndex = recent.Count > 0 ? 0 : -1;
        RecentButton.IsEnabled = RecentBox.IsEnabled = recent.Count > 0;
        if (recent.Count == 0)
            RecentButton.ToolTip = "There are no saved projects yet. Save one with the Project button in the main window.";
    }

    /// <summary>What to open once the main window is up; null for nothing (a blank project, or the window was simply closed).</summary>
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

    // ----- Dropping -----

    private static string? DroppedFile(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0]) ? files[0] : null;

    private void DropTarget_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // The target under the pointer lights up, so it is clear which preset a drop would use.
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

    private void Preset_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: EncodingPreset preset } button)
            return;

        button.Opacity = 1;
        if (DroppedFile(e) is not { } file)
            return;

        // Shift held while dropping asks again, for a preset whose answer was saved.
        var askAgain = e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey);

        // The question must not be asked inside the drop itself: Explorer waits, frozen, until the drop handler returns.
        Dispatcher.BeginInvoke(() => OpenWithPreset(file, preset, askAgain));
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EncodingPreset preset } && Browse() is { } file)
            OpenWithPreset(file, preset, askAgain: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private string? Browse()
    {
        var dialog = new OpenFileDialog { Title = "Select a video", Filter = VideoFilter };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    /// <summary>
    /// Asks which of the preset's settings to bring in, unless that was answered for this preset before with
    /// "Always use these settings for this preset" ticked.
    /// </summary>
    private void OpenWithPreset(string file, EncodingPreset preset, bool askAgain)
    {
        Activate();

        var settings = AppSettings.Current;
        if (askAgain || !settings.PresetImportChoices.TryGetValue(preset.Name, out var parts))
        {
            var known = settings.PresetImportChoices.TryGetValue(preset.Name, out var saved);
            var dialog = new PresetPartsDialog(preset.Name, Path.GetFileName(file), known ? saved : PresetParts.All, known) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            parts = dialog.Parts;
            if (dialog.Always)
                settings.PresetImportChoices[preset.Name] = parts;
            else
                settings.PresetImportChoices.Remove(preset.Name);

            if (dialog.Always || known)
            {
                try
                {
                    settings.SaveAsCurrent();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The choice still holds for this launch.
                }
            }
        }

        // With nothing ticked there is nothing of the preset to apply: the video opens the usual way.
        Finish(parts == PresetParts.None
            ? new LaunchRequest(LaunchKind.Video, file)
            : new LaunchRequest(LaunchKind.Video, file, preset.Name, parts));
    }
}
