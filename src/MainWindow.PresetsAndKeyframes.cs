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

// ----- Keyframe controls and lanes, pane layout, Add Layer kinds, mixer, preset and layout files, bottom bar,
// hover sprite sheet and master filmstrip -----
public partial class MainWindow
{
    // LUT selection lives on the Color Grade card (Color tab), not in this partial.

    // ----- Keyframes pane and bottom-bar keyframe buttons -----

    /// <summary>Keyframe target: the selected visual clip if still on the timeline, else the view model's ActiveKeyLayer.</summary>
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
        // Height changes come from the rows themselves (graph toggled); only width changes require a redraw.
        if (e.WidthChanged)
            DrawKeyTimeline();
    }

    private void KeyGraph_Click(object sender, RoutedEventArgs e) => DrawKeyTimeline();

    /// <summary>Zoom to Clip: sets the timeline view (and Keyframes rows) to the active clip's span plus a 4% margin.</summary>
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
    /// Feeds the Keyframes rows: timeline view range (zoom/scroll), shaded clip span, rows for All, each motion property and each animated
    /// effect value with diamonds, the selected row's graph, and the playhead. During playback only the playhead updates.
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

    /// <summary>Keyframe lanes for a clip: All, motion properties, and every keyframed effect value in its (non-bypassed) stack.</summary>
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

        // 3D tilt lanes (Pitch, Yaw) for tilted clips; Rotation already covers Roll.
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

    // ----- Pane layout -----
    // Pane placement, sizing and drag-to-dock are owned by DockManager (MainWindow.Docking.cs); these entry points only schedule a
    // rebuild for the active layout.

    private static readonly GridLength Star = new(1, GridUnitType.Star);

    /// <summary>Schedules a dock rebuild for the current layout and repositions the Views popup.</summary>
    private void ApplyPaneLayout()
    {
        _dock?.ScheduleRebuild();
        PlaceViewsPopup();
    }

    /// <summary>Side pane visibility changed: schedules a dock rebuild and a Keyframes redraw.</summary>
    private void UpdateSidePanes()
    {
        _dock?.ScheduleRebuild();
        Dispatcher.BeginInvoke(DrawKeyTimeline, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Pane borders are DockManager thumbs; no GridSplitter handlers live in MainWindow.

    // Pane title drags are DockManager drag-to-dock operations.

    // ----- Audio tab options: inline checkboxes collapse into a button when the row is too narrow -----

    private double _audioOptionsWidth;

    // ----- Lazily built tabs -----

    // Audio tab named parts, resolved from its template on first build. AudioPanel is queried externally (drop target) and may be null;
    // the rest are only touched from the tab's own events.
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

    // Clicking a popup's button to close it would immediately reopen it (StaysOpen=false closes on the press, then Click reopens). The
    // Views, bins and keyframe popups disable hit-testing on their PlacementTarget while open.
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

    private void AddVideo_Click(object sender, RoutedEventArgs e) => Faults.Forget(AddVideo_ClickAsync(sender, e), nameof(AddVideo_Click));

    private async Task AddVideo_ClickAsync(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a video to place on the frame",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.m2ts;*.wmv;*.flv;*.mpg;*.mpeg;*.gif|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.AddVideoLayerAsync(dialog.FileName);
    }

    private void AddAudio_Click(object sender, RoutedEventArgs e) => Faults.Forget(AddAudio_ClickAsync(sender, e), nameof(AddAudio_Click));

    private async Task AddAudio_ClickAsync(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a sound to add to the timeline",
            Filter = "Audio and video files|*.mp3;*.wav;*.m4a;*.flac;*.ogg;*.opus;*.aac;*.wma;*.mp4;*.mkv;*.mov;*.webm;*.avi|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.AddAudioLayerAsync(dialog.FileName, _viewModel.PositionMs / 1000);
    }

    /// <summary>Opens the Windows colour picker for the selected text layer's fill or outline colour (by button Tag).</summary>
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

        // Owner HWND wrapper so the WinForms dialog is modal over this window.
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

        // The native picker's input bypasses NoteUserInput, so record the undo step explicitly.
        _viewModel.Checkpoint($"change the {(outline ? "outline" : "text")} color of {layer.Name}");
        if (outline)
            layer.OutlineColor = color;
        else
            layer.FontColor = color;
    }

    // ----- Add Layer dispatch by flyout entry Tag -----
    // The flyout UI (button, timing, keys) belongs to Views/UnifiedTimelinePane; it closes itself and forwards the chosen entry here
    // (MainWindow.UnifiedTimeline.cs).

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
    /// Layers from a Mask: splits a full-frame mask (prompted when <paramref name="maskPath"/> is null) into one Cutout per HUD element,
    /// after review and merging in MaskLayersWindow.
    /// </summary>
    /// <param name="owner">Owner window: this one, or a dialog over it.</param>
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

        // Existing Cutout crop rects; mask pieces they already cover start deselected.
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
    /// Filters dialog subtitle: origin (Voiceovers-folder recording, video clip stream #1, or audio file stream #1) plus codec and
    /// channels when known.
    /// </summary>
    private static string SoundDescription(Layer layer)
    {
        var recorded = layer.IsAudio && layer.ImagePath.StartsWith(AppPaths.Voiceovers + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var origin = recorded ? "Voiceover Recording" : layer.IsVideoFile ? "Stream #1 (video clip)" : "Stream #1";
        if (!layer.IsVideoFile && layer.Name != layer.SourceFileName && !string.IsNullOrWhiteSpace(layer.Name))
            origin += $" ({layer.Name})";
        var format = layer.AudioCodec.Length > 0 ? (layer.AudioChannels.Length > 0 ? $"{layer.AudioCodec}, {layer.AudioChannels}" : layer.AudioCodec) : "mixed into the first track";
        return $"{origin}  ·  {format}";
    }

    /// <summary>
    /// Audio filters dialog for one clip (timeline row or menu), one Undo step. Output tracks have no filters: each Sub-Lane mixes itself,
    /// and an output track keeps only action, codec, bitrate and title.
    /// </summary>
    private void TrackFilters_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            // Audio file or video layer audio: same filter set, stored on the clip.
            case Layer layer:
                // Studio Voice mix keys are clip-relative; pass the playhead offset for Add Mix Key.
                var forClip = new TrackAudioFiltersDialog($"{layer.SourceFileName}   {SoundDescription(layer)}", layer.AudioFilters, _viewModel.PositionMs / 1000 - layer.StartTime) { Owner = this };
                if (forClip.ShowDialog() == true)
                {
                    _viewModel.Checkpoint($"change the audio filters of {layer.Name}");
                    layer.AudioFilters = forClip.Filters;
                    _viewModel.Log($"Changed the audio filters of {layer.SourceFileName}");
                }

                break;
        }
    }

    /// <summary>Opens Voiceover Studio (record, trim and place voiceovers mixed into the export).</summary>
    private void OpenVoiceoverStudio_Click(object sender, RoutedEventArgs e) => OpenVoiceoverStudio();

    /// <summary>Caption box appearance dialog (not a clip, so no Effects cards); changes form one Undo step, dropped if unchanged.</summary>
    private void ShowLayerProperties(Layer layer)
    {
        _viewModel.Checkpoint($"change the look of {layer.Name}");
        var visit = _viewModel.UndoMark;
        new LayerPropertiesDialog { Owner = this, DataContext = layer, ToggleVisibility = _viewModel.ToggleHidden }.ShowDialog();
        _viewModel.DropStepIfUnchanged(visit);
    }

    // ----- Layout files -----

    private const string LayoutFileFilter = "HandPeg Game Preset (*.hpstyle)|*.hpstyle|All files|*.*";


    /// <summary>Default layout file folder (AppPaths.Styles), created on first use so the file dialog can open there.</summary>
    private static string LayoutsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Styles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to the dialog's last-used folder.
        }

        return AppPaths.Styles;
    }

    // ----- Export Presets and Game Presets: save, import and export -----

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
    /// Small preset action dialog: optional save-to-list row with a name box (<paramref name="save"/>) and optional import/export buttons
    /// (<paramref name="files"/>). Export Presets use separate entries for each; Game Presets use one dialog for both.
    /// </summary>
    /// <param name="nameProperty">View model property bound to the save name box.</param>
    /// <returns>"save", "import", "export", or null when closed.</returns>
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
            // Save-to-list row: name box and default Save button.
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

    /// <summary>Export Presets "+ Save Current as Preset...": prompts for a name, then saves to My Presets.</summary>
    private void SaveCurrentPreset_Click(object sender, RoutedEventArgs e)
    {
        if (AskPresetAction("Save Current as Preset", "Saves the export settings as they are now (the size, frame rate, encoder, quality or bitrate, sound and format) to My Presets. A built-in preset's name is kept for it: the copy is saved as its name with (Custom).",
                nameof(MainViewModel.PresetName), files: false) == "save")
        {
            _viewModel.SavePresetCommand.Execute(null);
        }
    }

    /// <summary>Export Presets "Import / Export Preset...": imports a .hppreset file or exports current settings to one.</summary>
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

    // ----- Bottom bar -----

    /// <summary>
    /// Spans the output (destination) row across the full window width; status and progress live in the title bar and transport row.
    /// </summary>
    private void UpdateBottomBar()
    {
        Grid.SetColumn(DestinationBar, 0);
        Grid.SetColumnSpan(DestinationBar, 2);
    }

    private void ExportLayout_Click(object sender, RoutedEventArgs e)
    {
        // Choose the parts to include, then the destination file.
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

        // Choose which parts of the file to apply.
        var choice = new StyleImportDialog(layout, Path.GetFileName(dialog.FileName)) { Owner = this };
        if (choice.ShowDialog() == true)
            _viewModel.ApplyStyle(layout, choice.ImportLayout, choice.ImportColor, choice.ImportBlur, choice.ImportSubtitles);
    }

    // ----- Frame & Layer Engine dialogs -----

    // ----- Timeline hover previews -----

    // Source file's FFmpeg keyframe sprite sheet: Overview Scrubber fallback when the GPU program strip is unavailable
    // (OverviewSheet, MainWindow.ProgramStrip.cs).
    private ThumbnailSheet? _sourceSheet;

    // Load generation counter; a decode that completes after a newer request is discarded.
    private int _spriteSheetLoads;

    /// <summary>
    /// Decodes the sprite sheet off the UI thread into memory (no open file handle) and rebuilds the Overview strip. With no sheet (file
    /// closed or not yet generated) the strip clears immediately.
    /// </summary>
    private void LoadSpriteSheet() => Faults.Forget(LoadSpriteSheetAsync(), nameof(LoadSpriteSheet));

    private async Task LoadSpriteSheetAsync()
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
    /// Decodes the sheet on a worker from a stream (not a URI, which WPF's image cache would serve stale after regeneration) and freezes
    /// it; null when unreadable (no previews).
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
    /// Fills the filmstrip behind the timeline waveform from OverviewSheet (GPU program strip, else source keyframes): as many cells as
    /// fit at native aspect, each showing the midpoint time of its span.
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

    // Last sheet cell within the sequence duration (a source sheet is a full grid that may over- or under-run it).
    private static int LastPicture(ThumbnailSheet sheet, double duration) =>
        Math.Max(Math.Min(sheet.Count, (int)Math.Ceiling(duration / sheet.IntervalSeconds)) - 1, 0);

    private void TimelineSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (OverviewSheet is not { } sheet || !_viewModel.ShowHoverPreviews || _viewModel.DurationMs <= 0 || TimelineSlider.ActualWidth <= 0)
        {
            PreviewPopup.IsOpen = false;
            return;
        }

        // Slider track is inset by half a thumb at each end.
        const double inset = 5.5;
        var x = e.GetPosition(TimelineSlider).X;
        var fraction = Math.Clamp((x - inset) / (TimelineSlider.ActualWidth - 2 * inset), 0, 1);
        var duration = _viewModel.DurationMs / 1000;
        var seconds = fraction * duration;
        var (thumbnailWidth, thumbnailHeight) = (sheet.CellWidth, sheet.CellHeight);

        PreviewImage.Source = sheet.Cell(Math.Min(sheet.IndexAt(seconds), LastPicture(sheet, duration)));
        // Scale by the HoverPreviewZoom setting.
        var zoom = AppSettings.Current.HoverPreviewZoom;
        PreviewImage.Width = thumbnailWidth * zoom;
        PreviewImage.Height = thumbnailHeight * zoom;
        PreviewTime.Text = TimeDisplay.Format(seconds);

        // Centre horizontally on the pointer, just above the timeline.
        PreviewPopup.HorizontalOffset = x - thumbnailWidth * zoom / 2.0;
        PreviewPopup.VerticalOffset = -(thumbnailHeight * zoom + 40);
        PreviewPopup.IsOpen = true;
    }

    private void TimelineSlider_MouseLeave(object sender, MouseEventArgs e) => PreviewPopup.IsOpen = false;

    private static Point ClampTo(Rect area, Point point) =>
        new(Math.Clamp(point.X, area.Left, area.Right), Math.Clamp(point.Y, area.Top, area.Bottom));

    private static int Even(double value) => Math.Max((int)Math.Round(value / 2) * 2, 0);
}

/// <summary>
/// Live paused hover previews (Pass 5.6.5) for the Effects pane's Add Effect list, the Quick FX slots and the Fade In / Fade Out menus:
/// resting on an item for <see cref="DwellMs"/> while paused shows it on its clips (an effect card added, or the fade's style set, with
/// a default-length fade where there was none), seeking into the clip (or the fade) when the playhead is not on it; leaving the item,
/// closing the menu, choosing the item, or playback starting puts everything back as it was, playhead included unless it was moved
/// meanwhile. Nothing of a preview is an undo step: effect cards are look changes inside an owned look scope (no look auto-step), fade
/// styles are document edits made with undoable: false (the restore is too), so a chosen item's own step starts from the restored
/// state. Effects that switch a clip feature on (key, shadow, edges, blend mode, mask) are not previewed. UI thread only.
/// </summary>
internal sealed class PausedHoverPreview : IDisposable
{
    /// <summary>Rest (ms) on an item before its preview shows: sweeping across a menu previews nothing on the way.</summary>
    public const int DwellMs = 120;

    private readonly MainViewModel _vm;
    private readonly System.Windows.Threading.DispatcherTimer _dwell;

    // Undo actions of the preview showing, newest last; the pending preview waiting for its dwell.
    private readonly List<Action> _restore = [];
    private Action? _pending;

    // Playhead moved by the preview: where it was, and where the preview put it (ms).
    private (double From, double To)? _seek;

    public PausedHoverPreview(MainViewModel vm)
    {
        _vm = vm;
        _dwell = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(DwellMs) };
        _dwell.Tick += Dwell_Tick;
        _vm.PropertyChanged += Vm_PropertyChanged;
    }

    /// <summary>True while a preview is shown on the picture.</summary>
    public bool IsShowing => _restore.Count > 0;

    /// <summary>Pointer rests on an effect item: after the dwell, while paused, the effect is shown on <paramref name="layers"/>.</summary>
    public void HoverEffect(IReadOnlyList<Layer> layers, Models.Effects.EffectDefinition definition) =>
        Hover(Previewable(definition) ? () => ShowEffect(layers, definition) : null);

    /// <summary>Pointer rests on a transition style: after the dwell, while paused, the fade in (or out) of <paramref name="layers"/> takes it.</summary>
    public void HoverFade(IReadOnlyList<Layer> layers, bool fadeIn, TransitionStyle style) => Hover(() => ShowFade(layers, fadeIn, style));

    /// <summary>Pointer left the item (or the menu closed, or the item was chosen): the preview, if any, is undone.</summary>
    public void Leave()
    {
        _dwell.Stop();
        _pending = null;
        Restore();
    }

    public void Dispose()
    {
        Leave();
        _dwell.Tick -= Dwell_Tick;
        _vm.PropertyChanged -= Vm_PropertyChanged;
    }

    /// <summary>
    /// Effects a preview can add and take away cleanly: a card of its own, not a clip feature switched on (key, shadow, edges), a blend
    /// mode or a mask (which opens the mask editor).
    /// </summary>
    public static bool Previewable(Models.Effects.EffectDefinition definition) =>
        definition is { Pinned: false, Feature: null } && definition.Type != Models.Effects.EffectRegistry.BlendMode
        && definition.Type != Models.Effects.EffectRegistry.Mask;

    private void Hover(Action? show)
    {
        Leave();
        if (show is null || _vm.IsPlaying)
            return;

        _pending = show;
        _dwell.Start();
    }

    private void Dwell_Tick(object? sender, EventArgs e)
    {
        _dwell.Stop();
        var show = _pending;
        _pending = null;
        if (show is null || _vm.IsPlaying)
            return;

        try
        {
            show();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            AppLog.Write("Hover preview", ex);
            Restore();
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPlaying) && _vm.IsPlaying)
            Leave();
    }

    private void ShowEffect(IReadOnlyList<Layer> layers, Models.Effects.EffectDefinition definition)
    {
        var targets = layers.Where(l => l is { HasFilters: true, IsLoading: false, IsDeleted: false } && _vm.Layers.Contains(l) && l.CanTake(definition)).ToList();
        if (targets.Count == 0)
            return;

        foreach (var layer in targets)
        {
            Models.Effects.EffectInstance card;
            using (_vm.OwnLookScope())
                card = layer.AddEffect(definition.NewEffect());
            _restore.Add(() =>
            {
                using (_vm.OwnLookScope())
                    layer.Effects.Remove(card);
            });
        }

        if (_vm.ClipOf(targets[0]) is { } clip)
            SeekInto(clip.Start, clip.End, clip.Start + clip.Duration * 0.25);
    }

    private void ShowFade(IReadOnlyList<Layer> layers, bool fadeIn, TransitionStyle style)
    {
        var clips = layers.Where(l => l is { IsPicture: true, IsCaptions: false, IsLoading: false } && _vm.Layers.Contains(l))
            .Select(_vm.ClipOf).OfType<Models.Timeline.TimelineClip>().ToList();
        if (clips.Count == 0)
            return;

        // As the clips were, by id (an undo in between may replace the clip objects).
        var saved = clips.Select(c => (c.Id, c.FadeInSeconds, c.FadeOutSeconds, c.FadeInStyle, c.FadeOutStyle)).ToList();
        var standard = AppSettings.Current.DefaultTransitionSeconds;
        if (!_vm.Edit($"Preview {TransitionStyles.Label(style)}", edit =>
            {
                foreach (var clip in clips)
                {
                    var (own, other) = fadeIn ? (clip.FadeInSeconds, clip.FadeOutSeconds) : (clip.FadeOutSeconds, clip.FadeInSeconds);
                    var seconds = own >= 0.01 ? own : Math.Round(Math.Clamp(standard, 0, Math.Max(clip.Duration - other, 0)), 2);
                    edit.SetClipFades(clip, fadeIn ? seconds : clip.FadeInSeconds, fadeIn ? clip.FadeOutSeconds : seconds, fadeIn ? style : null, fadeIn ? null : style);
                }
            }, undoable: false))
            return;

        _restore.Add(() => _vm.Edit("Preview over", edit =>
        {
            foreach (var (id, fadeInSeconds, fadeOutSeconds, inStyle, outStyle) in saved)
            {
                if (_vm.Document.FindClip(id) is { } clip)
                    edit.SetClipFades(clip, fadeInSeconds, fadeOutSeconds, inStyle, outStyle);
            }
        }, undoable: false));

        // Into the middle of the first clip's fade.
        var first = clips[0];
        var fade = fadeIn ? first.FadeInSeconds : first.FadeOutSeconds;
        SeekInto(first.Start, first.End, fadeIn ? first.Start + fade / 2 : first.End - fade / 2, always: fade > 0.01);
    }

    // Moves the playhead to `seconds` when it is outside [start, end) (or always), remembering where it was.
    private void SeekInto(double start, double end, double seconds, bool always = false)
    {
        var at = _vm.PositionMs / 1000;
        if (!always && at >= start && at < end)
            return;

        var from = _vm.PositionMs;
        _vm.PositionMs = Math.Clamp(seconds, start, Math.Max(start, end - 0.001)) * 1000;
        _seek = (from, _vm.PositionMs);
    }

    private void Restore()
    {
        for (var i = _restore.Count - 1; i >= 0; i--)
        {
            try
            {
                _restore[i]();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                AppLog.Write("Hover preview restore", ex);
            }
        }

        _restore.Clear();
        if (_seek is { } seek && Math.Abs(_vm.PositionMs - seek.To) < 1)
            _vm.PositionMs = seek.From;
        _seek = null;
    }
}
