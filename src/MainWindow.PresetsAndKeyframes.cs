using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Models;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using Microsoft.Win32;

namespace HandPegApp;

// ----- MainWindow, continued: LUT files, the keyframe buttons and lanes, where the panes are, the Add Layer kinds, the mixer,
// presets and layout files, the bottom bar, the hover sprite sheet and the master filmstrip -----
public partial class MainWindow
{
    // ----- Filter files -----

    private void BrowseLut_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select a LUT", Filter = "3D LUT|*.cube|All files|*.*" };
        if (dialog.ShowDialog(this) == true)
            _viewModel.LutPath = dialog.FileName;
    }

    private void ClearLut_Click(object sender, RoutedEventArgs e) => _viewModel.LutPath = "";

    // ----- Keyframes: the pane, and the buttons of the bottom bar -----

    /// <summary>The clip the keyframe controls work on: the one selected on a time bar, or failing that the one the view model has.</summary>
    private Layer KeyClip => _selectedClip switch
    {
        Layer { IsPicture: true } layer when _viewModel.Layers.Contains(layer) => layer,
        _ => _viewModel.ActiveKeyLayer,
    };

    private void ToggleKeyframe_Click(object sender, RoutedEventArgs e)
    {
        var clip = KeyClip;
        if (_viewModel.HasKeyframeAtPlayhead(clip))
            _viewModel.RemoveKeyframe(clip);
        else
            _viewModel.AddKeyframe(clip);
    }

    private void PreviousKeyframe_Click(object sender, RoutedEventArgs e) => StepKeyframe(-1);

    private void NextKeyframe_Click(object sender, RoutedEventArgs e) => StepKeyframe(1);

    private void StepKeyframe(int direction)
    {
        if (PlayerIsPlaying)
            PlayerSetPause(true);
        _viewModel.GoToKeyframe(KeyClip, direction);
    }

    private void ClearKeyframes_Click(object sender, RoutedEventArgs e) => _viewModel.ClearKeys(KeyClip);

    private void KeyLanes_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // A change of height is the rows' own (the graph shown or hidden); only a new width lays them out again.
        if (e.WidthChanged)
            DrawKeyTimeline();
    }

    private void KeyGraph_Click(object sender, RoutedEventArgs e) => DrawKeyTimeline();

    /// <summary>Zoom to Clip: the time bars, and the Keyframes pane's rows with them, show the selected clip's stretch.</summary>
    private void ZoomToKeyClip_Click(object sender, RoutedEventArgs e)
    {
        var seconds = _viewModel.DurationMs / 1000;
        if (seconds <= 0)
            return;

        var clip = _viewModel.ActiveKeyLayer;
        var (from, to) = clip.GetSpan(seconds);
        var margin = Math.Max((to - from) * 0.04, 0.2);
        SetView(Math.Max(from - margin, 0), to - from + 2 * margin);
    }

    /// <summary>
    /// Draws the Keyframes pane's rows: the time bars' stretch of the sequence (their zoom and scroll), the selected
    /// clip's stretch of it shaded, a row for All and for each of its motion properties (and each animated effect value)
    /// with a diamond at each keyframe, the selected row's graph, and the playhead. The rows draw themselves from these;
    /// as the video plays only the playhead is moved.
    /// </summary>
    private void DrawKeyTimeline()
    {
        RefreshMotionPath();
        var seconds = _viewModel.DurationMs / 1000;
        if (!KeyframesPane.IsVisible || seconds <= 0 || KeyLanes.ActualWidth <= 0)
        {
            KeyLanes.Show([], 0, 0, null, null, double.NaN);
            return;
        }

        var clip = _viewModel.ActiveKeyLayer;
        var span = clip.GetSpan(seconds);
        ClampView();
        KeyLanes.ShowGraph = _viewModel.ShowKeyframeGraph;
        KeyLanes.Show(BuildKeyLanes(clip), _viewStart, ViewSpan, span, LayerClipBrush, _viewModel.PositionMs / 1000);
        _viewModel.SelectedKeyLane = KeyLanes.Selected?.Target;
    }

    /// <summary>The rows of a clip: All, its motion properties, and every effect value of its stack that has keyframes.</summary>
    private static List<KeyLane> BuildKeyLanes(Layer clip)
    {
        var start = clip.StartTime;
        var lanes = new List<KeyLane> { new() { Name = "All", IsAll = true, Times = [.. clip.KeyTimes.Select(t => t + start)] } };

        void Add(KeyProperty property, string name, Func<double, string> format)
        {
            var keys = clip.GetKeys(property);
            lanes.Add(new KeyLane
            {
                Name = name, Target = property, Format = format,
                Times = [.. keys.Select(k => k.Time + start)], Values = [.. keys.Select(k => k.Value)],
                ValueAt = time => clip.Evaluate(property, time - start),
            });
        }

        static string Percent(double fraction) => (fraction * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";
        Add(KeyProperty.X, "X", Percent);
        Add(KeyProperty.Y, "Y", Percent);
        Add(KeyProperty.Scale, "Scale", Percent);
        if (clip.HasFilters)
            Add(KeyProperty.Rotation, "Rotation", value => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "\u00B0");
        if (clip.IsPicture)
            Add(KeyProperty.Opacity, "Opacity", value => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%");

        // True 3D (5.2A): a tilted clip's Pitch and Yaw, as Rotation is its Roll.
        if (clip.HasFilters && clip.IsTilted)
        {
            Add(KeyProperty.TiltX, "Tilt X (Pitch)", value => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "\u00B0");
            Add(KeyProperty.TiltY, "Tilt Y (Yaw)", value => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "\u00B0");
        }

        foreach (var card in clip.EffectsBypassed ? [] : clip.Effects)
        {
            foreach (var param in card.Definition?.Params ?? [])
            {
                if (param.Animation == Models.Effects.ParamAnimation.None || param.Bind == nameof(Layer.Opacity) || !card.HasKeys(param.Key))
                    continue;

                var value = new EffectKeyRef(card, param, $"{card.Title}: {param.Label}");
                var keys = card.GetKeys(param.Key);
                lanes.Add(new KeyLane
                {
                    Name = value.Label, Target = value,
                    Times = [.. keys.Select(k => k.Time + start)], Values = [.. keys.Select(k => k.Value)],
                    ValueAt = time => MainViewModel.EffectValueAt(clip, value, time - start),
                    Format = number => number.ToString(param.Decimals == 0 ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture) + param.Unit,
                });
            }
        }

        return lanes;
    }

    // ----- The panes: where each is, and how large -----
    // The player area is two grids. The outer one has the player in one column and the side panes in another,
    // over the Sequence Overview Scrubber; the inner one has the video in one column and the layout pane in another.
    // Moving a pane is giving it the other row or column of its grid (and that row or column its size);
    // nothing is taken out of the window or put back. The arrangement belongs to the mode in use.

    private static readonly GridLength Star = new(1, GridUnitType.Star);

    /// <summary>Lays the panes out again, as this mode's arrangement has them (see MainWindow.Docking.cs).</summary>
    private void ApplyPaneLayout()
    {
        _dock?.ScheduleRebuild();
        PlaceViewsPopup();
    }

    /// <summary>A side pane was shown or hidden: the panes are laid out again.</summary>
    private void UpdateSidePanes()
    {
        _dock?.ScheduleRebuild();
        Dispatcher.BeginInvoke(DrawKeyTimeline, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // The border between the player and the Sequence Overview Scrubber. What it drags is the bar's height setting,
    // the one the slider beside the volume sets: the bar is as tall as that says, not as tall as a grid
    // row happens to be, so the row is put back to "as tall as its content" each time the splitter has sized it.

    // Dragging a pane by its title: let go on the other side of what it is beside, and it moves there.

    // ----- The Audio tab's checkboxes, folded into a button when the row is too narrow -----

    private double _audioOptionsWidth;

    // ----- Tabs that are built when they are first wanted -----

    // The named parts of the Audio tab that this file uses: the tab is made from a template, so these are found in
    // it when it is made. The one asked about from outside the tab (is a file being dropped on it) may not be there
    // yet; the rest are only reached from the tab's own events.
    private DockPanel? AudioPanel;
    private Grid AudioOptionsHost = null!;
    private StackPanel AudioOptionsInline = null!;
    private System.Windows.Controls.Primitives.ToggleButton AudioOptionsButton = null!;

    private void AudioOptionsHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AudioPanel is null)
            return;

        if (AudioOptionsInline.Visibility == Visibility.Visible && AudioOptionsInline.ActualWidth > 0)
            _audioOptionsWidth = Math.Max(_audioOptionsWidth, AudioOptionsInline.ActualWidth);
        if (_audioOptionsWidth <= 0)
        {
            AudioOptionsInline.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _audioOptionsWidth = AudioOptionsInline.DesiredSize.Width;
        }

        var fits = AudioOptionsHost.ActualWidth >= _audioOptionsWidth + 4;
        (AudioOptionsInline.Visibility, AudioOptionsButton.Visibility) = fits ? (Visibility.Visible, Visibility.Collapsed) : (Visibility.Hidden, Visibility.Visible);
        if (fits)
            AudioOptionsButton.IsChecked = false;
    }

    // A button that opened a menu would, clicked again to close it, open it straight back up (see
    // TimelineOptionsPopup_Opened). The views menu, the bins and the keyframe menu share the cure: each popup
    // knows its button (its placement target), which is deaf while the popup is open.
    private void BinPopup_Opened(object? sender, EventArgs e)
    {
        if (sender is Popup { PlacementTarget: UIElement button })
            button.IsHitTestVisible = false;
    }

    private void BinPopup_Closed(object? sender, EventArgs e)
    {
        if (sender is Popup { PlacementTarget: UIElement button })
            Dispatcher.BeginInvoke(() => button.IsHitTestVisible = true, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void RestoreDeleted_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BinItem item })
            _viewModel.RestoreFromBin(item);
        ScheduleClipRedraw();
    }

    private void EmptyBin_Click(object sender, RoutedEventArgs e) => _viewModel.EmptyBin();

    private async void AddVideo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a video to place on the frame",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.m2ts;*.wmv;*.flv;*.mpg;*.mpeg;*.gif|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.AddVideoLayerAsync(dialog.FileName);
    }

    private async void AddAudio_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a sound to add to the timeline",
            Filter = "Audio and video files|*.mp3;*.wav;*.m4a;*.flac;*.ogg;*.opus;*.aac;*.wma;*.mp4;*.mkv;*.mov;*.webm;*.avi|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.AddAudioLayerAsync(dialog.FileName, _viewModel.PositionMs / 1000);
    }

    /// <summary>Opens the Windows colour picker for one of the selected text layer's colours: its letters' or its outline's.</summary>
    private void TextColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string which } || _viewModel.SelectedTextLayer is not { } layer)
            return;

        var outline = which == "Outline";
        CaptionPreview.TryParse(outline ? layer.OutlineColor : layer.FontColor, out var current);
        using var picker = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
        };

        // Owned by this window, so that it opens over it and blocks it like any other dialog.
        var owner = new System.Windows.Forms.NativeWindow();
        owner.AssignHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        try
        {
            if (picker.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK)
                return;
        }
        finally
        {
            owner.ReleaseHandle();
        }

        var color = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        if (string.Equals(outline ? layer.OutlineColor : layer.FontColor, color, StringComparison.OrdinalIgnoreCase))
            return;

        // The Windows colour picker's clicks are not seen by the window (NoteUserInput), so its step is taken here (4.4).
        _viewModel.Checkpoint($"change the {(outline ? "outline" : "text")} color of {layer.Name}");
        if (outline)
            layer.OutlineColor = color;
        else
            layer.FontColor = color;
    }

    // ----- Add Layer's kinds: what each entry of the timeline's Add Layer flyout adds, by its Tag -----
    // The flyout itself (its button, its timing, its keys) is the timeline pane's own since 4.4 (Views/UnifiedTimelinePane);
    // it closes itself and hands the entry chosen on here (MainWindow.UnifiedTimeline.cs).

    private void AddLayerMenu_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "Empty": _viewModel.AddEmptyTrackCommand.Execute(null); break;
            case "Video": AddVideo_Click(sender, e); break;
            case "Image": AddImage_Click(sender, e); break;
            case "Text": _viewModel.AddTextLayerCommand.Execute(null); break;
            case "Adjustment": _viewModel.AddAdjustmentLayerCommand.Execute(null); break;
            case string vfx when vfx.StartsWith("builtin.", StringComparison.Ordinal): _viewModel.AddVfxClip(vfx); break;
            case "Region": _viewModel.AddLayerCommand.Execute(null); break;
            case "MaskLayers": AddLayersFromMask(); break;
            case "Audio": AddAudio_Click(sender, e); break;
            case "Voiceover": OpenVoiceoverStudio_Click(sender, e); break;
            case "Captions": GenerateCaptionsFrom(null); break;
            case "ImportSubtitles": ImportSubtitles(); break;
            case "EditCaptions": EditCaptions(); break;
            case "CaptionStyle": OpenCaptionStyle(); break;
        }
    }

    /// <summary>
    /// Layers from a Mask: a mask of the whole frame (chosen here when none is given) taken apart into one
    /// layer per HUD element it shows, after the pieces have been looked at and joined as wanted.
    /// </summary>
    /// <param name="owner">The window it opens over: this one, or a dialog open over it.</param>
    public void AddLayersFromMask(string? maskPath = null, Window? owner = null)
    {
        owner ??= this;
        if (!_viewModel.HasSource)
        {
            _viewModel.StatusText = "Load a video first: the Cutouts are cut out of it.";
            return;
        }

        if (maskPath is null)
        {
            var dialog = new OpenFileDialog { Title = "Select a mask of the whole frame", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*" };
            if (dialog.ShowDialog(owner) != true)
                return;
            maskPath = dialog.FileName;
        }

        // The areas already cut out of the videos (5.2-AuditB: pieces of their files, cut by their crops): pieces of the mask that one
        // of them covers start left out.
        var existing = _viewModel.Layers.Where(_viewModel.IsCutout)
            .Select(l =>
            {
                var (left, top, right, bottom) = Layer.HeldCrop(l.CropLeft, l.CropTop, l.CropRight, l.CropBottom);
                return new Rect(left, top, 1 - left - right, 1 - top - bottom);
            })
            .ToList();
        if (MaskLayersWindow.Open(_viewModel, maskPath, existing) is not { } window)
        {
            _viewModel.StatusText = "That mask shows nothing to make Cutouts of, or it could not be read: it should be white (or painted, on transparency) where the HUD is.";
            return;
        }

        window.Owner = owner;
        if (window.ShowDialog() == true)
            _viewModel.AddMaskLayers(maskPath, window.Pieces, window.Vertical, window.ReplaceExisting);
    }

    private void AddImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select an image to place on the frame", Filter = "Images|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(this) == true)
            _viewModel.AddImageLayer(dialog.FileName);
    }

    // ----- Audio mixer -----

    /// <summary>
    /// The filters of one sound, from its row on the timeline or its menu. One step of Undo. An output track has none (5.2A, the user's
    /// Q6: since 5.2-AuditB each Sub-Lane mixes itself, and an output track keeps only its action, codec, bitrate and title).
    /// </summary>
    private void TrackFilters_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            // A sound from a file, or the sound of a video layer: the same filters, kept with the clip.
            case Layer layer:
                // Studio Voice's mix keys are timed from the clip's start (5.2B): the playhead is where Add Mix Key puts one.
                var forClip = new TrackAudioFiltersDialog($"{layer.SourceFileName}   {layer.Description}", layer.AudioFilters, _viewModel.PositionMs / 1000 - layer.StartTime) { Owner = this };
                if (forClip.ShowDialog() == true)
                {
                    _viewModel.Checkpoint($"change the audio filters of {layer.Name}");
                    layer.AudioFilters = forClip.Filters;
                    _viewModel.Log($"Changed the audio filters of {layer.SourceFileName}");
                }

                break;
        }
    }

    /// <summary>The Voiceover Studio: recording, trimming and placing the voiceover that the encode mixes in.</summary>
    private void OpenVoiceoverStudio_Click(object sender, RoutedEventArgs e) => OpenVoiceoverStudio();

    /// <summary>The caption box's look (it is no clip, so it has no cards in the Effects pane): what is changed in it is one step of Undo.</summary>
    private void ShowLayerProperties(Layer layer)
    {
        _viewModel.Checkpoint($"change the look of {layer.Name}");
        var visit = _viewModel.UndoMark;
        new LayerPropertiesDialog { Owner = this, DataContext = layer, ToggleVisibility = _viewModel.ToggleHidden }.ShowDialog();
        _viewModel.DropStepIfUnchanged(visit);
    }

    private void LiveFramePreview_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasSource)
        {
            _viewModel.StatusText = "Load a video first.";
            return;
        }

        // The player would only keep running behind a dialog that shows a still frame.
        if (PlayerIsPlaying)
            PlayerSetPause(true);

        // Each still is the GPU engine's capture (5.2-Arm64AndNpu), asked of the engine running then: one that starts, or starts
        // again, while the dialog is open draws the next.
        new FramePreviewDialog(_viewModel, at => _gpu?.CaptureFrameAsync(at) ?? Task.FromResult<(byte[] Bgra, int Width, int Height)?>(null)) { Owner = this }.ShowDialog();
    }

    // ----- Layout files -----

    private const string LayoutFileFilter = "HandPeg Game Preset (*.hpstyle)|*.hpstyle|All files|*.*";


    /// <summary>Where layout files are kept unless the user picks somewhere else. Made on first use, so the dialog can open in it.</summary>
    private static string LayoutsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Styles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The dialog then opens wherever Windows last had it.
        }

        return AppPaths.Styles;
    }

    // ----- Export Presets and Game Presets: saved, and moved to and from files (5.4B: the Export Presets selector, the Timeline's "...") -----

    private const string PresetFileFilter = "HandPeg export preset|*.hppreset;*.json|All files|*.*";

    private void ImportPreset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import Export Preset", Filter = PresetFileFilter };
        if (dialog.ShowDialog(this) == true)
            _viewModel.ImportPreset(dialog.FileName);
    }

    private void ExportPreset_Click(object sender, RoutedEventArgs e)
    {
        var name = _viewModel.PresetName.Trim().Length > 0 ? _viewModel.PresetName.Trim() : "My Preset";
        var dialog = new SaveFileDialog { Title = "Export Preset to a File", Filter = PresetFileFilter, FileName = name + ".hppreset", DefaultExt = "hppreset" };
        if (dialog.ShowDialog(this) == true)
            _viewModel.ExportPreset(dialog.FileName);
    }

    /// <summary>
    /// The one button of a preset block: a small dialog with what can be done with that kind of preset, each
    /// as a button that does it: saving to the list under a name typed there (<paramref name="save"/>), and moving one to or from a
    /// file (<paramref name="files"/>). The Export Presets have a button for each (5.4B: + Save Current as Preset..., Import / Export
    /// Preset...); Game Presets one for both.
    /// </summary>
    /// <param name="nameProperty">The view model's property that holds the name a preset of this kind is saved to the list under.</param>
    /// <returns>"save", "import", "export", or null when the dialog was closed.</returns>
    private string? AskPresetAction(string title, string about, string nameProperty, bool save = true, bool files = true)
    {
        string? chosen = null;
        var dialog = new Window
        {
            Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        dialog.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        dialog.SetResourceReference(ForegroundProperty, "TextBrush");
        dialog.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(dialog);

        var panel = new StackPanel { Margin = new Thickness(22), Width = 380 };
        var text = new TextBlock { Text = about, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        panel.Children.Add(text);

        Button Choice(string label, string action, string tip)
        {
            var button = new Button { Content = label, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Left, ToolTip = tip };
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.Click += (_, _) =>
            {
                chosen = action;
                dialog.Close();
            };
            return button;
        }

        if (save)
        {
            // Saving to the list: the name, and the button that saves under it.
            panel.Children.Add(new TextBlock { Text = "Save the current settings to the list as:", Margin = new Thickness(0, 0, 0, 4) });
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var saveButton = Choice("Save", "save", "Saves the current settings to the list under this name. An existing name is replaced.");
            (saveButton.Margin, saveButton.IsDefault) = (new Thickness(6, 0, 0, 0), true);
            DockPanel.SetDock(saveButton, Dock.Right);
            var name = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(name, "Preset Name");
            name.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding(nameProperty) { Source = _viewModel, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
            row.Children.Add(saveButton);
            row.Children.Add(name);
            panel.Children.Add(row);
            dialog.Loaded += (_, _) => name.Focus();
        }

        if (files)
        {
            panel.Children.Add(Choice("Import from a File...", "import", "Brings a preset in from a file."));
            panel.Children.Add(Choice("Export to a File...", "export", "Saves the settings as they are now to a file."));
        }

        var close = new Button { Content = "Close", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
        panel.Children.Add(close);
        dialog.Content = panel;
        dialog.ShowDialog();
        return chosen;
    }

    /// <summary>The Export Presets selector's "+ Save Current as Preset...": the name asked for, then the settings saved to My Presets under it.</summary>
    private void SaveCurrentPreset_Click(object sender, RoutedEventArgs e)
    {
        if (AskPresetAction("Save Current as Preset", "Saves the export settings as they are now (the size, frame rate, encoder, quality or bitrate, sound and format) to My Presets. A built-in preset's name is kept for it: the copy is saved as its name with (Custom).",
                nameof(MainViewModel.PresetName), files: false) == "save")
        {
            _viewModel.SavePresetCommand.Execute(null);
        }
    }

    /// <summary>The Export Presets selector's "Import / Export Preset...": a preset brought in from a .hppreset file, or the settings saved to one.</summary>
    private void ManagePresets_Click(object sender, RoutedEventArgs e)
    {
        switch (AskPresetAction("Import / Export Preset", "An Export Preset is the export's size, frame rate, encoder, quality or bitrate, sound and format. Bring one in from a .hppreset file (it joins My Presets and is applied), or save the settings as they are now to one.",
                    nameof(MainViewModel.PresetName), save: false))
        {
            case "import":
                ImportPreset_Click(sender, e);
                break;
            case "export":
                ExportPreset_Click(sender, e);
                break;
        }
    }

    private void ManageStyles_Click(object sender, RoutedEventArgs e)
    {
        switch (AskPresetAction("Game Presets", "A Game Preset is the video clips' layout and their Cutouts with their masks, and the color, blur and subtitle settings that go with them, in one .hpstyle file. Save the ones set now to the list, or move one to or from a file.", nameof(MainViewModel.StyleName)))
        {
            case "save":
                _viewModel.SaveStyle();
                break;
            case "import":
                ImportLayout_Click(sender, e);
                break;
            case "export":
                ExportLayout_Click(sender, e);
                break;
        }
    }

    // ----- The bottom of the window -----

    /// <summary>
    /// The output row across the bottom of the window (its whole width since the status bar beside it was retired, 5.4.1-M5: the status
    /// and the progress are the title bar's and the transport row's).
    /// </summary>
    private void UpdateBottomBar()
    {
        Grid.SetColumn(DestinationBar, 0);
        Grid.SetColumnSpan(DestinationBar, 2);
    }

    private void ExportLayout_Click(object sender, RoutedEventArgs e)
    {
        // First which parts, then where to.
        var choice = new StyleImportDialog(_viewModel.CaptureStyleWithMasks(), fileName: null) { Owner = this };
        if (choice.ShowDialog() != true)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Export Game Preset", Filter = LayoutFileFilter, FileName = "My Game Preset.hpstyle", DefaultExt = "hpstyle",
            InitialDirectory = LayoutsFolder(),
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.ExportStyle(dialog.FileName, choice.ImportLayout, choice.ImportColor, choice.ImportBlur, choice.ImportSubtitles);
    }

    private void ImportLayout_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import Game Preset", Filter = LayoutFileFilter, InitialDirectory = LayoutsFolder() };
        if (dialog.ShowDialog(this) != true || _viewModel.ReadStyle(dialog.FileName) is not { } layout)
            return;

        // Which parts of the file to take.
        var choice = new StyleImportDialog(layout, Path.GetFileName(dialog.FileName)) { Owner = this };
        if (choice.ShowDialog() == true)
            _viewModel.ApplyStyle(layout, choice.ImportLayout, choice.ImportColor, choice.ImportBlur, choice.ImportSubtitles);
    }

    // ----- Frame & Layer Engine dialogs -----

    // The eye on a layer's row: its whole track is hidden or shown (as Hide / Show on its menu).
    private void LayerVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Layer { CanHide: true } layer })
            _viewModel.ToggleHidden(layer);
    }

    // ----- Timeline hover previews -----

    // The opened file's key frames (FFmpeg's hover sheet): what the Sequence Overview Scrubber shows where the GPU cannot draw the
    // sequence's own pictures (OverviewSheet, MainWindow.ProgramStrip.cs).
    private ThumbnailSheet? _sourceSheet;

    // Which load of the thumbnail sheet is the latest: a sheet decoded after a newer one was asked for is not wanted.
    private int _spriteSheetLoads;

    /// <summary>
    /// Loads the thumbnail sheet into memory (so the file itself is not kept open), decoded away from the interface's
    /// thread, and redraws the Sequence Overview Scrubber's strip with it. With no sheet (a file closed, or one without a sheet yet)
    /// the strip is cleared at once (4.4: it kept the last file's pictures until the next change of length).
    /// </summary>
    private async void LoadSpriteSheet()
    {
        var load = ++_spriteSheetLoads;
        _sourceSheet = null;
        PreviewPopup.IsOpen = false;
        var sheet = _viewModel.SpriteSheetPath is { } path ? await DecodeSpriteSheetAsync(path) : null;
        if (load != _spriteSheetLoads)
            return;

        const int grid = FfmpegRunner.SpriteGridSize;
        _sourceSheet = sheet is { PixelWidth: >= grid, PixelHeight: >= grid } && _viewModel.SpriteIntervalSeconds > 0
            ? new ThumbnailSheet(sheet, grid, grid * grid, sheet.PixelWidth / grid, sheet.PixelHeight / grid, _viewModel.SpriteIntervalSeconds, [])
            : null;
        RebuildFilmstrip();
    }

    /// <summary>
    /// The thumbnail sheet, read from a stream (never by its address: WPF's image cache would hand back the sheet that
    /// was at that path before it was made again) and frozen, on a worker; null when it cannot be read, which just means
    /// no previews.
    /// </summary>
    private static async Task<BitmapSource?> DecodeSpriteSheetAsync(string path)
    {
        try
        {
            return await WorkScheduler.Run(new WorkSpec("Timeline thumbnails", WorkLane.Interactive) { Visible = false, Cancellable = false },
                _ => Task.Run<BitmapSource?>(() =>
                {
                    if (!File.Exists(path))
                        return null;

                    try
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.StreamSource = stream;
                        image.EndInit();
                        image.Freeze();
                        return image;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FormatException)
                    {
                        return null;
                    }
                }));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // ----- Timeline thumbnails -----

    private void FilmstripList_SizeChanged(object sender, SizeChangedEventArgs e) => RebuildFilmstrip();

    /// <summary>
    /// Fills the strip behind the timeline's waveform with pictures cut from the thumbnail sheet (the sequence's own, drawn
    /// by the GPU engine, else the opened file's key frames): as many as fit at their own shape, each showing the moment at
    /// the middle of the stretch of timeline it lies over.
    /// </summary>
    private void RebuildFilmstrip()
    {
        var duration = _viewModel.DurationMs / 1000;
        if (!_viewModel.ShowTimelineThumbnails || OverviewSheet is not { } sheet || duration <= 0)
        {
            FilmstripList.ItemsSource = null;
            FilmstripList.Visibility = Visibility.Collapsed;
            return;
        }

        FilmstripList.Visibility = Visibility.Visible;
        var stripWidth = FilmstripList.ActualWidth > 0 ? FilmstripList.ActualWidth : TimelineSlider.ActualWidth;
        var stripHeight = FilmstripList.ActualHeight > 0 ? FilmstripList.ActualHeight : Math.Max(_viewModel.TimelineAreaHeight - 6, 8);
        var count = Math.Clamp((int)Math.Round(stripWidth / (stripHeight * sheet.CellWidth / sheet.CellHeight)), 1, 80);
        var last = LastPicture(sheet, duration);

        var pictures = new List<ImageSource>(count);
        for (var i = 0; i < count; i++)
            pictures.Add(sheet.Cell(Math.Min(sheet.IndexAt((i + 0.5) / count * duration), last)));

        FilmstripList.ItemsSource = pictures;
    }

    // The last picture of a sheet that lies within the sequence (the opened file's sheet is a whole grid, longer or shorter than it).
    private static int LastPicture(ThumbnailSheet sheet, double duration) =>
        Math.Max(Math.Min(sheet.Count, (int)Math.Ceiling(duration / sheet.IntervalSeconds)) - 1, 0);

    private void TimelineSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (OverviewSheet is not { } sheet || !_viewModel.ShowHoverPreviews || _viewModel.DurationMs <= 0 || TimelineSlider.ActualWidth <= 0)
        {
            PreviewPopup.IsOpen = false;
            return;
        }

        // The track is shorter than the slider by half a thumb at each end.
        const double inset = 5.5;
        var x = e.GetPosition(TimelineSlider).X;
        var fraction = Math.Clamp((x - inset) / (TimelineSlider.ActualWidth - 2 * inset), 0, 1);
        var duration = _viewModel.DurationMs / 1000;
        var seconds = fraction * duration;
        var (thumbnailWidth, thumbnailHeight) = (sheet.CellWidth, sheet.CellHeight);

        PreviewImage.Source = sheet.Cell(Math.Min(sheet.IndexAt(seconds), LastPicture(sheet, duration)));
        // Shown larger or smaller than generated, as set in Settings.
        var zoom = AppSettings.Current.HoverPreviewZoom;
        PreviewImage.Width = thumbnailWidth * zoom;
        PreviewImage.Height = thumbnailHeight * zoom;
        PreviewTime.Text = TimeDisplay.Format(seconds);

        // Centred on the pointer, sitting just above the timeline.
        PreviewPopup.HorizontalOffset = x - thumbnailWidth * zoom / 2.0;
        PreviewPopup.VerticalOffset = -(thumbnailHeight * zoom + 40);
        PreviewPopup.IsOpen = true;
    }

    private void TimelineSlider_MouseLeave(object sender, MouseEventArgs e) => PreviewPopup.IsOpen = false;

    private static Point ClampTo(Rect area, Point point) =>
        new(Math.Clamp(point.X, area.Left, area.Right), Math.Clamp(point.Y, area.Top, area.Bottom));

    private static int Even(double value) => Math.Max((int)Math.Round(value / 2) * 2, 0);
}
