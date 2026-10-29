using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;
using HandPegApp.ViewModels;
using Microsoft.Win32;

namespace HandPegApp;

// Captions and Remove Dead Air from the main window (4.4): what the Subtitles tab held, now reached from the timeline's
// caption row and track menus, the Add Layer menu and the commands: generating captions (the Auto-Captions dialog),
// importing and editing them, their style and their box, and Remove Dead Air by the sound or by the captions.
public partial class MainWindow
{
    /// <summary>
    /// Remove Dead Air on what is given: a video layer or a sound clip, an audio track, or (null) the main video. The dialog
    /// asks how (by the sound's silences or by the gaps between captioned words, 4.4) and whether to mark or delete.
    /// </summary>
    internal void RunDeadAir(object? target)
    {
        // A clip of the main video is the main video: heard through its file's tracks, and cut on every unlocked track.
        if (target is Layer { IsMainVideo: true })
            target = null;

        // With nothing chosen, the main video listens to its Voice track when one is marked (4.3E).
        var name = (target ?? _viewModel.AudioTracks.FirstOrDefault(t => t.IsVoice)) switch
        {
            Layer layer => layer.Name,
            AudioTrack track => $"the video, listening to {track.Title}",
            _ => "the video",
        };
        var dialog = new DeadAirDialog(name) { Owner = this };
        if (dialog.ShowDialog() == true)
            _viewModel.RunDeadAir(dialog.Mode, target, dialog.Source);
    }

    /// <summary>
    /// Generate Auto-Captions, from wherever it is asked (a track's or a clip's menu, the caption row, the Add Layer menu):
    /// the Auto-Captions dialog opens on the sound given (one of the video's own tracks, a sound clip), or with none on what
    /// is set (the voice clip, the Voice track); what is chosen there is applied and the captions are made at once.
    /// </summary>
    internal void GenerateCaptionsFrom(object? target)
    {
        if (target is Layer { CarriesSound: false } silent)
        {
            _viewModel.StatusText = $"{silent.Name} has no sound to make captions from.";
            return;
        }

        if (!_viewModel.HasSource)
        {
            _viewModel.StatusText = "Load a video first: there is nothing to make captions from.";
            return;
        }

        var dialog = new AutoCaptionsDialog(_viewModel, target) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Source is not { } source)
            return;

        _viewModel.ChooseCaptionSource(source);
        _viewModel.UseWhisperModel(dialog.Model);
        (_viewModel.WhisperLanguage, _viewModel.WhisperTranslate, _viewModel.WhisperPrompt) = (dialog.SpokenLanguage, dialog.Translate, dialog.Prompt);
        if (_viewModel.GenerateCaptionsCommand.CanExecute(null))
            _viewModel.GenerateCaptionsCommand.Execute(null);
    }

    /// <summary>
    /// The captions' menu, for the caption row, the subtitle track's header and its mini-stack: making, editing, styling,
    /// placing, importing and deleting the captions, and whether they are drawn into the export.
    /// </summary>
    /// <param name="target">What the menu belongs to (a track of the video, a clip with sound), for Generate Auto-Captions; null for what is set.</param>
    internal void FillCaptionMenu(ItemsControl menu, object? target)
    {
        var hasSource = _viewModel.HasSource;

        Add("Generate Auto-Captions...", () => GenerateCaptionsFrom(target), hasSource,
            $"Transcribes the speech and puts the captions on the timeline, where they follow the clips they were heard in. Listening to: {_viewModel.CaptionSourceSummary}");
        Add("Edit Captions...", EditCaptions, hasSource && (_viewModel.HasSubtitleCues || _viewModel.AutoCaptions),
            "Opens the Subtitle Editor: the words, their times and where each word begins. With no captions yet, they are made first.");
        Add("Import Subtitles...", ImportSubtitles, true,
            "Reads an .srt, .vtt, .ass or picture subtitle file in as the subtitle track, replacing the one there is.");
        menu.Items.Add(new Separator());

        var burn = new MenuItem
        {
            Header = "Burn Captions into Export",
            IsCheckable = true,
            IsChecked = _viewModel.AutoCaptions,
            ToolTip = "Draws the captions into the picture of the export (and shows them in the player), in the caption style, in the caption box.",
        };
        burn.Click += (_, _) => _viewModel.AutoCaptions = burn.IsChecked;
        menu.Items.Add(burn);
        Add("Caption Style...", OpenCaptionStyle, true, "The font, its size and colours, the outline, and how the words appear (Karaoke Sweep, TikTok Pop).");
        Add("Caption Position & Look...", () => ShowLayerProperties(_viewModel.CaptionLayer), true,
            "The caption box as a layer: its opacity, corners, soft edges and drop shadow. Move and size it on the Canvas.");
        Add("Restore Caption Position", () => _viewModel.ResetCaptionLayerCommand.Execute(null), true,
            "Puts the caption box back where it starts: across the lower part of the frame.");
        menu.Items.Add(new Separator());
        Add("Delete Subtitle Track", () => _viewModel.ClearSubtitlesCommand.Execute(null), _viewModel.HasSubtitleCues,
            "Deletes the subtitle track and the captions on it (Undo brings them back). Captions are then transcribed again when the video is exported.");

        void Add(string header, Action action, bool enabled, string tip)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, ToolTip = tip };
            ToolTipService.SetShowOnDisabled(item, true);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    /// <summary>Import Subtitles: reads a subtitle file in as the subtitle track.</summary>
    internal void ImportSubtitles()
    {
        var dialog = new OpenFileDialog { Title = "Select subtitles to import", Filter = Services.SubtitleFiles.ImportFilter };
        if (dialog.ShowDialog(this) == true)
            _viewModel.ImportSubtitles(dialog.FileName);
    }

    /// <summary>Edit Captions: the Subtitle Editor on the subtitle track.</summary>
    internal void EditCaptions() => _ = OpenSubtitleEditorAsync();

    /// <summary>Caption Style: the style's dialog; what is changed there is one step of Undo.</summary>
    internal void OpenCaptionStyle()
    {
        var dialog = new CaptionStyleDialog(_viewModel.CaptionStyle) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.Checkpoint("change the caption style");
            _viewModel.SetCaptionStyle(dialog.EditedStyle);
        }
    }

    /// <summary>
    /// Opens the Subtitle Editor on the subtitle track. With no track yet and captions on, the track is first made from
    /// them, so that what was transcribed is what is edited. While the editor is open the main player draws no subtitles:
    /// the editor's own player does.
    /// </summary>
    private async Task OpenSubtitleEditorAsync()
    {
        if (_viewModel.IsSubtitleEditorOpen)
            return;

        if (!_viewModel.HasSubtitleCues && _viewModel.AutoCaptions && _viewModel.HasSource)
            await _viewModel.ConvertCaptionsToCuesAsync();

        if (PlayerIsPlaying)
            PlayerSetPause(true);
        _viewModel.IsSubtitleEditorOpen = true;
        try
        {
            new Views.SubtitleEditorWindow(_viewModel) { Owner = this }.ShowDialog();
        }
        finally
        {
            _viewModel.IsSubtitleEditorOpen = false;
            _viewModel.ReprojectCaptions();
            ScheduleClipRedraw();
        }
    }
}
