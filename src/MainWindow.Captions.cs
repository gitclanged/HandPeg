using System.Windows;
using System.Windows.Controls;
using HandPegApp.Models;
using HandPegApp.ViewModels;
using Microsoft.Win32;

namespace HandPegApp;

// Captions and Remove Dead Air entry points, reached from the timeline caption row and track menus, the Add Track menu and commands:
// Auto-Captions generation, subtitle import and editing, caption style and box, and Remove Dead Air by audio or by caption gaps.
public partial class MainWindow
{
    /// <summary>
    /// Remove Dead Air on <paramref name="target"/>: a clip with its own audio, a video (its file's clips, via the first stream), or null for
    /// the Voice-marked audio, else the video at the playhead. The dialog chooses the source (audio silences or gaps between captioned
    /// words) and mode (mark or delete).
    /// </summary>
    internal void RunDeadAir(object? target)
    {
        var dialog = new DeadAirDialog(_viewModel.DeadAirTargetName(target)) { Owner = this };
        if (dialog.ShowDialog() == true)
            _viewModel.RunDeadAir(dialog.Mode, target, dialog.Source);
    }

    /// <summary>
    /// Generate Auto-Captions from any entry point (track or clip menu, caption row, Add Track menu): opens the Auto-Captions dialog on
    /// the given audio (a source track or audio clip), or with null on the configured voice clip or Voice track; applies the chosen
    /// source, model and language and generates captions immediately.
    /// </summary>
    internal void GenerateCaptionsFrom(object? target)
    {
        if (target is Layer { CarriesSound: false } silent && _viewModel.CaptionStreamsOf(target) is not { Count: > 0 })
        {
            _viewModel.StatusText = $"{silent.Name} has no sound to make captions from.";
            return;
        }

        if (!_viewModel.HasSource && target is null)
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
    /// Builds the captions menu for the caption row, the subtitle track header and its mini-stack: generate, edit, style, position,
    /// import and delete captions, and toggle burn-in for export.
    /// </summary>
    /// <param name="target">Menu owner (a source track or audio clip) for Generate Auto-Captions; null for the configured source.</param>
    internal void FillCaptionMenu(ItemsControl menu, object? target)
    {
        var hasSource = _viewModel.HasSource;

        Add("Generate Auto-Captions...", () => GenerateCaptionsFrom(target), hasSource,
            $"Transcribes the speech and puts the captions on the timeline, where they follow the clips they were heard in. Listening to: {_viewModel.CaptionSourceSummary}");
        Add("Edit Captions...", EditCaptions, hasSource && (_viewModel.HasSubtitleCues || _viewModel.AutoCaptions),
            "Opens the Subtitle Studio: the words, their times and where each word begins. With no captions yet, they are made first.");
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
            "The caption box as a clip: its opacity, corners, soft edges and drop shadow. Move and size it on the Canvas.");
        Add("Restore Caption Position", () => _viewModel.ResetCaptionLayerCommand.Execute(null), true,
            "Puts the caption box back where it starts: across the lower part of the frame.");

        // Caption stacking: below a given track or above everything. The 3D view's plane lift (Shift + drag) does the same.
        var onTop = _viewModel.CaptionsOnTop;
        Add("Bring Captions Forward", () => _viewModel.MoveCaptionsInStack(1), hasSource && !onTop,
            $"Puts the captions over the next track up (over everything from the top one). {_viewModel.CaptionsPlaceText()}");
        Add("Send Captions Back", () => _viewModel.MoveCaptionsInStack(-1), hasSource && _viewModel.CaptionsPlace > 0,
            $"Puts the captions under the next track down: a HUD or a picture over them covers them, in the preview and in every export. {_viewModel.CaptionsPlaceText()}");
        Add("Captions Over Everything", () => _viewModel.PutCaptionsAt(int.MaxValue, "put the captions over everything"), hasSource && !onTop,
            "Puts the captions back over every track and the fades, where they start.");
        Add("Split Caption at Playhead", () => SplitCaptionAt(_viewModel.PositionMs / 1000), hasSource && _viewModel.HasTextCues,
            "Splits the caption under the playhead in two at the word there: the words said before it stay, the rest are a caption of their own.");
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

    /// <summary>
    /// Splits the caption at <paramref name="seconds"/> on the word boundary there (Split at Playhead with captions selected, the razor
    /// on a caption block, the captions menu), then schedules a row redraw.
    /// </summary>
    private void SplitCaptionAt(double seconds)
    {
        _viewModel.StatusText = _viewModel.SplitCaptionAt(seconds);
        Services.CrashReporter.Note("timeline", "split a caption");
        ScheduleClipRedraw();
    }

    /// <summary>Import Subtitles: loads a subtitle file as the subtitle track.</summary>
    internal void ImportSubtitles()
    {
        var dialog = new OpenFileDialog { Title = "Select subtitles to import", Filter = Services.SubtitleFiles.ImportFilter };
        if (dialog.ShowDialog(this) == true)
            _viewModel.ImportSubtitles(dialog.FileName);
    }

    /// <summary>Edit Captions: opens the Subtitle Studio on the subtitle track.</summary>
    internal void EditCaptions() => _ = OpenSubtitleStudioAsync();

    /// <summary>Caption Style: opens the style dialog; an accepted change is one Undo step.</summary>
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
    /// Opens the Subtitle Studio dock pane on the subtitle track. With no track yet and captions on, the track is first built from the
    /// captions so the transcription is what gets edited. The main player keeps rendering captions and the main Undo keeps working:
    /// the studio commits each gesture as a step (MainViewModel.CommitSubtitleEdits).
    /// </summary>
    private async Task OpenSubtitleStudioAsync()
    {
        if (!_viewModel.HasSubtitleCues && _viewModel.AutoCaptions && _viewModel.HasSource)
            await _viewModel.ConvertCaptionsToCuesAsync();

        OpenStudio(SubtitleStudioId);
    }
}
