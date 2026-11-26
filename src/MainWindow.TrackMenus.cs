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
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using Microsoft.Win32;

namespace HandPegApp;

// ----- Track context menus (FillTrackMenu, FillLayerMenu) and their items -----
public partial class MainWindow
{
    // ----- Track context menu -----
    // One menu for every timeline row: UnifiedTimelineControl.MenuRequested supplies row, pressed block (or fade handle, or header) and
    // FillTrackMenu adds the currently valid actions. Naming: Title Case; "..." only when a dialog follows; shortcuts shown beside items
    // (KeybindManager), never in names; Delete = content, Remove = attribute, Clear = collection, Restore = bring back.

    /// <summary>
    /// Appends a timeline row's context-menu items: track-level (filters, position, mask, look, audio) and clip-level (split, trim, fades,
    /// speed, delete). A fade handle gets only that fade's menu.
    /// </summary>
    /// <param name="rowData">Row Data (a Layer for video, image or audio tracks, the caption row or blur fill row); null below the rows.</param>
    /// <param name="clicked">Pressed block's Clip (Layer, LayerSound, SubtitleRef), or null.</param>
    /// <param name="fade">Pressed fade handle: its clip and whether it is the fade in.</param>
    /// <param name="onHeader">The header was right-clicked: the menu targets the track (and its selected clip).</param>
    internal void FillTrackMenu(ItemsControl menu, object? rowData, object? clicked, (object Clip, bool In)? fade, bool onHeader)
    {
        // Fade handle: only that fade's menu.
        if (!onHeader && fade is { } pressed && FadeMenuItems(pressed.Clip, pressed.In) is { Count: > 0 } fadeItems)
        {
            foreach (var item in fadeItems)
                menu.Items.Add(item);
            return;
        }

        var first = menu.Items.Count;
        switch (rowData)
        {
            case Layer layer:
                FillLayerMenu(menu, layer, onHeader ? null : clicked, onHeader);
                break;
            case EffectLane:
                FillEffectMenu(menu, clicked as EffectBlock);
                break;
            default:
                return;
        }

        TidySeparators(menu, first);
    }

    /// <summary>
    /// Track row menu (Layer: video, image or audio track, caption row, blur fill row) in edit order: flat Cut &amp; Edit, then one submenu
    /// per clip aspect (transform, Game Preset, effects, audio, time), flat Smart Tools, and track-level items last.
    /// </summary>
    /// <param name="onHeader">Header right-click: track items (Track Preset) come first instead of in the Track submenu.</param>
    /// <param name="slim">
    /// A clip body's right-click (Pass 5.7.1): the core verbs (Split, Ripple Delete, Delete, Copy, Cut, Paste) stay at the top and the rest
    /// goes into Edit, Transform &amp; Look and Tools &amp; Track beside the aspect submenus: a dozen items at most.
    /// </param>
    private void FillLayerMenu(ItemsControl menu, Layer layer, object? clicked, bool onHeader = false, bool slim = false)
    {
        // Target clip: the clicked one, else the selected one on this track, else the track's first.
        var clips = _viewModel.GetTrackClips(layer);
        var clip = (clicked ?? _selectedClip) switch
        {
            Layer chosen when clips.Contains(chosen) => chosen,
            LayerSound sound when clips.Contains(sound.Layer) => sound.Layer,
            _ => layer,
        };

        void Add(string header, Action action, bool enabled = true, string? tip = null, string? gesture = null) =>
            AddTrackItem(menu, header, action, enabled, tip, gesture);
        void Separate() => AddSeparator(menu);
        void Keep(MenuItem submenu)
        {
            TidySeparators(submenu, 0);
            if (submenu.Items.Count > 0)
                menu.Items.Add(submenu);
        }

        // The slim menu's secondary groups (null: flat in the menu, as the header and older callers have it).
        slim = slim && !onHeader && !layer.IsCaptions && !layer.IsEmptyTrack;
        var (edit, look, tools) = slim
            ? (new MenuItem { Header = "Edit" }, new MenuItem { Header = "Transform & Look" }, new MenuItem { Header = "Tools & Track" })
            : ((MenuItem?)null, (MenuItem?)null, (MenuItem?)null);
        ItemsControl editTo = edit ?? (ItemsControl)menu, lookTo = look ?? (ItemsControl)menu, toolsTo = tools ?? (ItemsControl)menu;
        void AddIn(ItemsControl to, string header, Action action, bool enabled = true, string? tip = null, string? gesture = null) =>
            AddTrackItem(to, header, action, enabled, tip, gesture);
        var (editAt, lookAt) = (-1, -1);

        // Multi-selection member: group actions first, each one Undo step.
        if (GroupHolds && _viewModel.IsInClipGroup(clip))
        {
            AddGroupItems(editTo);
            AddSeparator(editTo);
        }

        var at = _viewModel.PositionMs / 1000;

        // Empty track: only Track Preset (settable before any drop), leaving its mini-stack, or delete.
        if (layer.IsEmptyTrack)
        {
            AddTrackPresetItems(menu, layer, flat: false);
            menu.Items.Add(ManageGamePresetsItem());
            Separate();
            AddUnlinkItem(menu, layer, layer);
            Add("Delete Track", () => _viewModel.RemoveTrack(layer));
            return;
        }

        // Header menu targets the track: Track Preset first.
        if (onHeader && _viewModel.CanHaveTrackPreset(layer))
        {
            AddTrackPresetItems(menu, layer, flat: false);
            menu.Items.Add(ManageGamePresetsItem());
            Separate();
        }

        // Inline audio name box at the top (one Undo step from the first keystroke). On a stream Sub-Lane it names the lane, which is
        // also the output track title.
        if (layer.IsAudio)
        {
            if (_viewModel.StreamLaneName(layer) is { } laneName)
                AddRenameItem(menu, laneName, RenameWithUndo(laneName, name => _viewModel.RenameStreamLane(layer, name)), menu.Items.Count);
            else
                AddRenameItem(menu, clip.Title, RenameWithUndo(clip.Name, name => clip.Title = name), menu.Items.Count);
            Separate();
        }

        // Caption row: captions menu (generate, edit, style, position, import, delete).
        if (layer.IsCaptions)
        {
            FillCaptionMenu(menu, null);
            Separate();
        }

        var editable = layer.IsRemovable;

        // ----- Cut & Edit -----
        if (layer.HasTiming)
        {
            Add("Split at Playhead", () => _viewModel.SplitLayer(clip, at), gesture: "edit.split");
            Add("Ripple Delete", () => _viewModel.RippleDelete(clip), gesture: "edit.rippleDelete");
        }

        if (editable)
        {
            if (clips.Count > 1)
                Add("Delete", () => DeleteMenuClip(clip), gesture: "edit.delete");
            else
                Add("Delete", () => _viewModel.RemoveTrack(layer), gesture: "edit.delete");
        }

        // Clipboard: this clip, or the multi-selection.
        if (_viewModel.CanGroup(clip))
        {
            Add("Copy", () => { SelectWithGroup(clip); CopySelectedClips(); }, gesture: "edit.copy");
            Add("Cut", () => { SelectWithGroup(clip); CutSelectedClips(); }, gesture: "edit.cut");
            AddIn(editTo, "Duplicate", () => { SelectWithGroup(clip); DuplicateSelectedClips(); }, gesture: "edit.duplicate",
                tip: "A copy of this clip (or the clips selected with it), with its Cutouts, right after it on the same track where there is room.");

            // Move to its own track with its stream audio and Cutouts, one Undo step.
            if (!clip.IsAudio)
            {
                var movable = _viewModel.CanMoveClipsToNewTrack([clip]);
                AddIn(editTo, "Move to New Track Above", () => { SelectWithGroup(clip); MoveSelectedToNewTrack(above: true); }, movable, gesture: "clip.newTrackAbove",
                    tip: "Puts this clip (or the clips selected with it) on a new track just above its own, with its stream sounds and Cutouts; a clip alone on its track goes above the next track up.");
                AddIn(editTo, "Move to New Track Below", () => { SelectWithGroup(clip); MoveSelectedToNewTrack(above: false); }, movable, gesture: "clip.newTrackBelow",
                    tip: "Puts this clip (or the clips selected with it) on a new track just below its own, with its stream sounds and Cutouts; a clip alone on its track goes below the next track down.");
            }

            AddIn(editTo, "Paste Attributes...", () => { SelectWithGroup(clip); PasteAttributesToSelection(); }, _viewModel.HasCopiedAttributes, gesture: "edit.pasteAttributes",
                tip: "Gives this clip (or the clips selected with it) the parts you choose of the copied clip's look: its transform, crop, opacity, effects, keyframes, mask, fades or sound.");
        }

        if (_viewModel.HasCopiedClips && layer.HasTiming)
            Add("Paste at Playhead", () => _viewModel.PasteClips(at), gesture: "edit.paste");
        editAt = menu.Items.Count;
        if (editable && !slim)
            Add("Duplicate Track", () => _viewModel.DuplicateLayer(layer), tip: "A copy of this whole track, every clip on it, on a new track just above, moved a little on the frame.");

        // Audio on a shared lane or Audio track: move to its own lane under its video's track, or to its own track.
        if (layer.IsAudio && _viewModel.ClipOf(clip) is not null)
        {
            if (_viewModel.ParentRowOf(layer) is { } videoRow && _viewModel.CanMoveSoundUnder(clip, videoRow))
                AddIn(editTo, "Move to Its Own Sub-Lane", () => _viewModel.MoveSoundToOwnLane(clip, videoRow),
                    tip: "Takes this sound off the lane it shares onto a lane of its own in the same track's audio stack, at the same time. Dragging it onto a video track's row does the same there.");
            AddIn(editTo, "Move to a New Audio Track", () => _viewModel.MoveSoundToOwnLane(clip, null),
                tip: "Takes this sound onto an Audio track of its own, at the same time; it still follows its video clip until it is unlocked from it.");
        }

        // Restore Folded Silence at this clip's seams (later content slides out), and Hide again for unfolded ranges over it (content
        // slides back).
        if (layer.HasTiming)
        {
            foreach (var fold in _viewModel.FoldsAt(clip))
            {
                AddIn(editTo, string.Create(CultureInfo.InvariantCulture, $"Restore Folded Silence ({fold.Length:0.##} s) at Its {(fold.AtEnd ? "End" : "Start")}"),
                    () => AnimatedFoldChange(() => _viewModel.UnfoldSegment(fold)));
            }

            foreach (var restored in _viewModel.RestoredFoldsOn(clip))
            {
                AddIn(editTo, string.Create(CultureInfo.InvariantCulture, $"Hide / Fold Dead Air Segment ({restored.Length:0.##} s)"),
                    () => AnimatedFoldChange(() => _viewModel.HideRestoredStretch(restored)),
                    tip: "Folds out again the stretch of dead air that was put back here: what follows closes up, and its seam can put it back once more.");
            }

            if (_viewModel.FoldCount is > 1 and var folds)
                AddIn(editTo, string.Create(CultureInfo.InvariantCulture, $"Restore All {folds} Folded Silences"), () => AnimatedFoldChange(_viewModel.UnfoldAll));
        }

        // Block switches as menu items: On/Off for this clip only, and a child's Lock to its parent.
        if (_viewModel.ClipOf(clip) is not null && !layer.IsCaptions)
        {
            var on = _viewModel.IsClipOn(clip);
            AddIn(editTo, on ? clip.IsAudio ? "Turn Off (Mute This Clip)" : "Turn Off (Hide This Clip)" : "Turn On", () => _viewModel.SetClipOn(clip, !on),
                tip: "This clip alone: its Cutouts and effects keep their own switches. The track header's switch takes the whole track and its stack.");
            if (_viewModel.ClipLock(clip) is { } locked)
            {
                AddIn(editTo, locked ? "Unlock from Parent Clip" : "Lock to Parent Clip", () => _viewModel.SetClipLocked(clip, !locked),
                    tip: locked ? "Frees it: it is dragged, slid and trimmed on its own, and its clip no longer takes it along."
                        : "Locks it to the clip it lies over: it moves, trims and splits with it.");
            }
        }

        Separate();
        lookAt = menu.Items.Count;

        // ----- Transform, Sizing & Position -----
        if (clip is { HasPlacement: true, IsCaptions: false } && _viewModel.ClipOf(clip) is not null)
            lookTo.Items.Add(TransformMenu(clip, clips.Count));

        // ----- Cutout & Mask Studio (every video clip: top-level item and first in the submenu) -----
        var isVideoClip = clip.IsVideoFile;
        const string StudioTip = "The Cutout Studio on this clip: trace its shape or a game's HUD by hand on a still, zoomed in to the very pixels; "
                                 + "cut this clip to it, add the Cutouts to it, or save them as a Game Preset.";
        if (isVideoClip && !slim)
            Add("Open Cutout & Mask Studio...", () => OpenCutoutStudio(clip), tip: StudioTip);

        // ----- Game Presets & Cutout Studio -----
        var presets = new MenuItem { Header = "Game Presets & Cutout Studio" };
        if (isVideoClip)
            AddTrackItem(presets, "Open Cutout & Mask Studio...", () => OpenCutoutStudio(clip), tip: StudioTip);
        if (_viewModel.GamePresetTargets([clip]).Count > 0)
            presets.Items.Add(GamePresetMenu(clip));
        presets.Items.Add(ManageGamePresetsItem());
        if (layer.HasMask)
        {
            AddTrackItem(presets, "Mask from a File or a Ready Shape...", () => OpenMaskTool(clip),
                tip: "The shape this clip alone is cut to: a picture file, one of the ready shapes, one drawn in the Cutout Studio, or none.");
        }

        if (_viewModel.IsCutout(clip))
            AddTrackItem(presets, "Draw Target", () => _viewModel.DrawTargetCommand.Execute(clip));
        if (clip is { CustomMask: true } && _viewModel.IsCutout(clip) && File.Exists(clip.MaskPath.Trim().Trim('"')))
            AddTrackItem(presets, "Add More Cutouts of This Mask...", () => AddLayersFromMask(clip.MaskPath.Trim().Trim('"')));
        if (isVideoClip && _viewModel.ClipOf(clip) is not null)
        {
            var blur = new MenuItem
            {
                Header = "Blur Fill",
                IsCheckable = true,
                IsChecked = _viewModel.IsBlurFilled(clip),
                ToolTip = "Fills the frame around this clip with a blurred copy of its whole picture, under it (the look of the blur fill's settings row). It costs one more decoder while it plays, none while the clip covers the frame.",
            };
            blur.Click += (_, _) =>
            {
                RunGuarded("Blur Fill", () => _viewModel.SetBlurFill(clip, blur.IsChecked));
                ScheduleClipRedraw();
            };
            presets.Items.Add(blur);
        }

        if (look is not null)
            KeepIn(look, presets);
        else
            Keep(presets);

        // ----- Effects & Transitions -----
        var effects = new MenuItem { Header = "Effects & Transitions" };
        if ((layer.HasFilters || layer.IsBackground) && AddEffectMenu(layer.IsBackground ? layer : clip) is { } addEffect)
            effects.Items.Add(addEffect);
        if (layer.HasFilters)
            AddTrackItem(effects, "Filters...", () => OpenLayerTool(layer, LayerTool.Filters));
        if (layer.IsBackground)
            AddTrackItem(effects, "Blur Fill Settings...", ShowBlurFillSettings, tip: "Its radius, zoom, passes and dimming, on its row in the Effects pane.");
        else if (layer is { HasStyle: true, IsCaptions: false })
            AddTrackItem(effects, "Show in Effects Pane", () => ShowInEffectsPane(clip), tip: "Opacity, rounded corners, soft edges, a drop shadow, a chroma key: each an effect in the Effects pane.");

        if (layer.HasFilters || layer.IsBackground)
        {
            AddSeparator(effects);
            AddTrackItem(effects, "Copy Effects", () =>
            {
                SelectForMenu(clip);
                EffectsView.CopyEffects();
            });
            AddTrackItem(effects, "Paste Effects", () =>
            {
                SelectForMenu(clip);
                EffectsView.PasteCopiedEffects();
            }, EffectsView is { HasCopiedEffects: true });
        }

        if (layer.HasTiming)
        {
            AddSeparator(effects);

            // Its fades (over lower layers; handles at its top corners) and crossfades with overlapping clips on other tracks.
            AddFadeSubmenus(effects, clip);

            // Crossfades across its shared cuts with the previous and next clips on its track.
            var transition = AppSettings.Current.DefaultTransitionSeconds;
            if (_viewModel.ClipBefore(clip) is { } previous)
            {
                if (clip.TransitionIn > 0)
                    AddTrackItem(effects, string.Create(CultureInfo.InvariantCulture, $"Remove Crossfade from {previous.Name} ({clip.TransitionIn:0.##} s)"), () => _viewModel.SetCrossfade(clip, 0));
                else
                    AddTrackItem(effects, string.Create(CultureInfo.InvariantCulture, $"Crossfade from {previous.Name} ({transition:0.##} s)"), () => _viewModel.SetCrossfade(clip, transition));
            }

            if (clips.FirstOrDefault(c => ReferenceEquals(_viewModel.ClipBefore(c), clip)) is { } following)
            {
                if (following.TransitionIn > 0)
                    AddTrackItem(effects, string.Create(CultureInfo.InvariantCulture, $"Remove Crossfade into {following.Name} ({following.TransitionIn:0.##} s)"), () => _viewModel.SetCrossfade(following, 0));
                else
                    AddTrackItem(effects, string.Create(CultureInfo.InvariantCulture, $"Crossfade into {following.Name} ({transition:0.##} s)"), () => _viewModel.SetCrossfade(following, transition));
            }
        }

        if (clip.IsAnimated)
        {
            AddSeparator(effects);
            AddTrackItem(effects, "Clear Keyframes", () => _viewModel.ClearKeys(clip));
        }

        Keep(effects);

        // ----- Audio & Voice -----
        // Clip gain, track filters, A/V link (J/L cuts, slips) and ducking. Omitted for video-only children (blur fill, HUD Cutout,
        // mask-cut piece).
        var audio = new MenuItem { Header = "Audio & Voice" };
        var linkable = !clip.IsAudio && _viewModel.ClipOf(clip) is { HasSound: true };
        if (!layer.IsVideoOnlyChild && !clip.IsVideoOnlyChild && (layer.CarriesSound || clip.CarriesSound || linkable))
        {
            if (clip.CarriesSound)
                audio.Items.Add(GainMenu(clip.GainDb, gain => _viewModel.SetClipGain(clip, gain)));
            if (layer.CarriesSound)
                AddTrackItem(audio, "Audio Filters...", () => OpenAudioFilters(layer));
            if (linkable)
                AddLinkItem(audio, clip, _viewModel.ClipOf(clip) is { AudioLinked: true });
            if (layer.CarriesSound)
                AddDuckingItems(audio, layer, layer.AutoDuck, layer.IsVoice);
        }

        // Auto-Level Speech (as a gain-line double-click) and beat detection: both measure in the background, one Undo step. Beats are
        // stored on the clip; drags snap to them and the beat grid draws them (both toggled in the Snap button's menu).
        if (!layer.IsVideoOnlyChild && !clip.IsVideoOnlyChild && _viewModel.ClipOf(clip) is { HasSound: true })
        {
            AddSeparator(audio);
            AddTrackItem(audio, "Auto-Level Speech (-14 LUFS)", () => _ = AutoLevelSpeechAsync(clip), _viewModel.CanAutoLevelSpeech(clip),
                "Measures this clip's speech, then sets its gain to -14 LUFS.");
            AddTrackItem(audio, "Detect & Snap to Audio Beats", () => _ = DetectBeatsAsync(clip), _viewModel.CanDetectBeats(clip),
                "Finds the beats of this clip's sound (in the background) and keeps them with the clip: dragged clips' edges snap to them, and the beat grid shows them as ticks along the clip. One step of Undo.");
            if (_viewModel.HasBeats(clip))
                AddTrackItem(audio, "Clear Audio Beats", () => _viewModel.ClearClipBeats(clip), true, "Takes this clip's beats away. One step of Undo.");
        }

        // Stream lane: include or exclude its output audio track (output tracks are defined by stream index) in the export.
        if (layer.IsAudio && _viewModel.OutputTrackOf(layer) is { } output)
        {
            AddSeparator(audio);
            var dropped = new MenuItem
            {
                Header = $"Leave Out of the Export ({output.Title})",
                IsCheckable = true,
                IsChecked = output.IsDropped,
                ToolTip = "The export's audio track for this stream place (every lane of it, on any track) is left out of the file. Unticked, it is encoded again.",
            };
            dropped.Click += (_, _) => RunGuarded("an output track", () => output.IsMuted = dropped.IsChecked);
            audio.Items.Add(dropped);
        }

        // Any video track can split all its audio onto separate lanes (no primary-track privilege).
        if (editable && clip.IsVideoFile && _viewModel.CanUnpackStreams(clip))
        {
            AddTrackItem(audio, "Unpack Audio Streams", () => _ = _viewModel.UnpackAudioStreamsAsync(clip), true,
                "Gives each audio stream of this file (game, desktop, microphone) a lane of its own in the track's audio stack, with its own mute, gain, filters and voice switch. One step of Undo.");
        }

        Keep(audio);

        // ----- Speed & Time -----
        var time = new MenuItem { Header = "Speed & Time" };

        // Common speeds (up to 16x) with the current one ticked; other values via the Speed box or the Effects pane's Speed & Time card.
        // Rippling follows that card's Ripple Timeline setting.
        if (clip.HasSpeed)
        {
            var speeds = new MenuItem { Header = string.Create(CultureInfo.InvariantCulture, $"Clip Speed ({clip.Speed:0.##}x)") };
            foreach (var speed in new[] { 0.1, 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 4, 10, 16 })
            {
                var item = new MenuItem
                {
                    Header = string.Create(CultureInfo.InvariantCulture, $"{speed:0.##}x{(speed == 1 ? "  (As Recorded)" : "")}"),
                    IsCheckable = true, IsChecked = Math.Abs(clip.Speed - speed) < 0.0005,
                };
                item.Click += (_, _) =>
                {
                    RunGuarded("a clip's speed", () => _viewModel.SetClipSpeed(clip, speed));
                    ScheduleClipRedraw();
                };
                speeds.Items.Add(item);
            }

            time.Items.Add(speeds);
        }

        if (layer.HasTiming)
        {
            AddTrackItem(time, "Split / Trim by Timecode...", () => SplitTrimByTimecode(clip));
            AddTrackItem(time, "Trim Start to Playhead", () => _viewModel.TrimClip(clip, start: true, at), gesture: "edit.trimStart");
            AddTrackItem(time, "Trim End to Playhead", () => _viewModel.TrimClip(clip, start: false, at), gesture: "edit.trimEnd");
            AddTrackItem(time, "Extend to Whole Sequence", () => _viewModel.ShowThroughout(clip),
                clip.StartTime > 0.001 || clip.StartTime + clip.Duration < _viewModel.SequenceSeconds - 0.001,
                clip.IsAudio ? "Plays the sound from the start of the sequence to its end." : "Shows the clip from the start of the sequence to its end.");
        }

        // On-demand proxy (ProxyCache), offered only when useful: a long-GOP video too slow to decode, a clip already using one, or Shift
        // held while opening the menu.
        if (clip.IsVideoFile && _viewModel.OptimizeIsUseful(clip, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            AddSeparator(time);
            var optimized = clip.ProxyChoice == ProxyChoice.Make;
            var optimize = new MenuItem
            {
                Header = "Optimize Long-GOP Clip for Scrubbing", IsCheckable = true, IsChecked = optimized,
                ToolTip = $"{_viewModel.ProxyStatus(clip)}. Makes a small proxy of this clip's file (its I-frames close together: Settings, Proxies), in the background, which the GPU preview plays in its place, so that seeking and scrubbing a recording whose I-frames are far apart cost little where the graphics card cannot decode it fast. Exports always read the original.",
            };
            optimize.Click += (_, _) => RunGuarded("a clip's proxy", () => _viewModel.SetProxyChoice(clip, optimized ? ProxyChoice.Auto : ProxyChoice.Make));
            time.Items.Add(optimize);
        }

        Keep(time);
        Separate();

        // ----- Smart Tools -----
        // Personal Clip Library: lossless copy of the clip's span, available across projects.
        if (_viewModel.CanSaveToClipLibrary(clip))
            AddIn(toolsTo, "Save Clip to Personal Clip Library...", () => SaveToClipLibrary(clip), true,
                "Keeps this clip's span (its picture and every sound of its file, a lossless copy: nothing encoded again) in your own library, Documents\\HandPeg\\Clips, with a title, tags and the focal point of a Ping-to-Mark highlight inside it. The Media pane's Clip Library lists it, to search, scrub and drag into any project.");

        // Side-by-side recording split: gameplay and facecam as two synced clips laid out for the Sequence aspect.
        if (editable && _viewModel.CanUnpackSideBySide(clip))
            AddIn(toolsTo, "Unpack OBS Side-by-Side Canvas (Gameplay + Facecam)", () => _viewModel.UnpackSideBySide(clip), true,
                "This recording is two pictures side by side on one canvas. The clip keeps the left half (the gameplay), cut to it and placed for the Sequence's shape, and a Cutout of the same file above it shows the right half (the facecam): a corner picture on a wide frame, a band across the top of a tall one. The two stay in step through every split, trim and move. One step of Undo.");

        if (editable && layer is { HasFilters: true, IsAdjustment: false, IsBackground: false })
            AddIn(toolsTo, "3D Fly-In + Shockwave Impact", () => _viewModel.FlyInWithShockwave(clip), _viewModel.HasSource,
                "From the playhead the clip flies out to the middle of the frame, lands with a shockwave and a camera shake, and glides back. It is all keyframes on the clip and an Impact clip: change anything. One step of Undo.");

        if (editable)
        {
            // Audio analysis targets: captions (own audio, or a video's stream Sub-Lanes) and silence detection.
            if (clip.CarriesSound)
            {
                AddIn(toolsTo, "Generate Auto-Captions...", () => GenerateCaptionsFrom(clip), _viewModel.HasSource);
                AddCaptionsSubmenu(toolsTo, clip, () => _viewModel.UseLayerAudioForCaptions(clip));
            }
            else if (clip.IsVideoFile && _viewModel.CaptionStreamsOf(clip) is { Count: > 0 })
            {
                AddIn(toolsTo, "Generate Auto-Captions...", () => GenerateCaptionsFrom(clip), _viewModel.HasSource);
                AddCaptionsSubmenu(toolsTo, clip, null);
            }

            // Remove Dead Air on any video or audio track. Clips with their own audio analyse and cut themselves; a video with stream
            // Sub-Lanes analyses its first (or voice-marked) stream and cuts its file's clips on every unlocked track, Cutouts included;
            // a Cutout has no audio of its own.
            if (clip.CarriesSound)
            {
                AddIn(toolsTo, "Remove Dead Air...", () => RunDeadAir(clip), _viewModel.HasSource,
                    "Finds the silences in this clip's own sound (or the gaps between its captioned words) and cuts them out of it: Split & Mark folds them out of its track (each can be put back from the clip's menu), or they are deleted.");
            }
            else if (clip.IsVideoFile && !_viewModel.IsCutout(clip) && _viewModel.CaptionStreamsOf(clip) is { Count: > 0 })
            {
                AddIn(toolsTo, "Remove Dead Air...", () => RunDeadAir(clip), _viewModel.HasSource,
                    "Finds the silences in this video's sound (the stream marked as the voice, else its first) and cuts them out of every clip of its file on every unlocked track at once, their Cutouts and the other clips with them.");
            }
            else if (clip.IsVideoFile)
            {
                toolsTo.Items.Add(DeadAirWithoutSound(clip));
            }
        }

        Separate();

        // ----- Track-level items -----
        var track = new MenuItem { Header = "Track" };
        if (!onHeader)
        {
            AddTrackPresetItems(track, layer, flat: false);
            AddSeparator(track);
        }

        if (layer.CanReorder && !layer.IsAudio)
        {
            AddTrackItem(track, "Bring Forward", () => _viewModel.MoveLayerUpCommand.Execute(layer), tip: "Moves this track one place towards the front: it is drawn over the track above it now.");
            AddTrackItem(track, "Send Backward", () => _viewModel.MoveLayerDownCommand.Execute(layer), tip: "Moves this track one place towards the back: it is drawn under the track below it now.");
        }

        AddUnlinkItem(track, clip, clips[0]);
        if (editable && slim)
            AddTrackItem(track, "Duplicate Track", () => _viewModel.DuplicateLayer(layer), tip: "A copy of this whole track, every clip on it, on a new track just above, moved a little on the frame.");
        if (editable || layer.IsBackground)
            AddTrackItem(track, layer.IsHidden ? "Show" : layer.IsAudio ? "Mute" : "Hide", () => _viewModel.ToggleHidden(layer));
        if (editable && clips.Count > 1)
        {
            AddSeparator(track);
            AddTrackItem(track, "Delete Track", () => _viewModel.RemoveTrack(layer), tip: $"Deletes the whole track, its {clips.Count} clips with it (Undo brings them back).");
        }

        if (tools is null)
        {
            Keep(track);
            return;
        }

        // Slim: the secondary groups where they read best (Edit after the core verbs, Transform & Look before the aspect submenus).
        AddSeparator(tools);
        KeepIn(tools, track);
        TidySeparators(tools, 0);
        if (tools.Items.Count > 0)
            menu.Items.Add(tools);
        foreach (var (group, index) in new[] { (look!, lookAt), (edit!, editAt) })
        {
            TidySeparators(group, 0);
            if (group.Items.Count > 0)
                menu.Items.Insert(Math.Clamp(index, 0, menu.Items.Count), group);
        }
    }

    /// <summary>Adds a submenu to a menu when it has items (its separators tidied first).</summary>
    private static void KeepIn(ItemsControl menu, MenuItem submenu)
    {
        TidySeparators(submenu, 0);
        if (submenu.Items.Count > 0)
            menu.Items.Add(submenu);
    }

    /// <summary>
    /// Transform, Sizing &amp; Position submenu: inline numeric fields and one-click placements, bound to the same values as the inspector
    /// strip and Canvas; each change is one Undo step for this clip only.
    /// </summary>
    private MenuItem TransformMenu(Layer clip, int clipsOnTrack)
    {
        var menu = new MenuItem { Header = "Transform, Sizing & Position" };
        void Number(string label, Func<double> read, Action<double> write) =>
            AddNumberRow(menu, label, read, write, $"change {label.ToLowerInvariant()} of {clip.Name}");

        Number("Width %", () => clip.SizePercent, v => clip.SizePercent = (int)Math.Round(v));
        if (clip.IsRemovable)
        {
            Number("Height %", () => clip.SizeHeightPercent, v =>
            {
                clip.LockAspectRatio = false;
                clip.SizeHeightPercent = (int)Math.Round(v);
            });
        }

        Number("Position X %", () => clip.PositionXPercent, v => clip.PositionXPercent = v);
        Number("Position Y %", () => clip.PositionYPercent, v => clip.PositionYPercent = v);
        if (clip.HasFilters)
        {
            // Yaw/Pitch/Roll: Roll is Rotation; the tilts rotate out of the frame plane in perspective.
            Number("Rotation (Roll) \u00B0", () => clip.Rotation, v => clip.Rotation = v);
            Number("Tilt X (Pitch) \u00B0", () => clip.TiltX, v => clip.TiltX = Layer.Tilt(v));
            Number("Tilt Y (Yaw) \u00B0", () => clip.TiltY, v => clip.TiltY = Layer.Tilt(v));
        }

        var crop = new MenuItem { Header = "Crop" };
        void Edge(string label, Func<double> read, Action<double> write) =>
            AddNumberRow(crop, label, read, write, $"crop {clip.Name}");
        Edge("Left %", () => clip.CropLeftPercent, v => clip.CropLeftPercent = v);
        Edge("Right %", () => clip.CropRightPercent, v => clip.CropRightPercent = v);
        Edge("Top %", () => clip.CropTopPercent, v => clip.CropTopPercent = v);
        Edge("Bottom %", () => clip.CropBottomPercent, v => clip.CropBottomPercent = v);

        // Interactive Crop and Reset Crop for this clip. The check reflects the global mode (whichever clip started it), so any clip's
        // menu can turn it off (as does Esc).
        AddSeparator(crop);
        if (clip.IsFile)
        {
            var wasOn = _viewModel.IsInteractiveCropActive;
            var interactive = new MenuItem
            {
                Header = "Interactive Crop", IsCheckable = true, IsChecked = wasOn,
                ToolTip = wasOn
                    ? "Interactive Crop is on. Choose it to stop (or press Esc)."
                    : "Drag a rectangle over the video in the player to set this clip's crop. Double-click inside it to centre it. Choose it again, or press Esc, to stop.",
            };
            interactive.Click += (_, _) => _viewModel.CropInteractively(clip, !wasOn);
            crop.Items.Add(interactive);
        }

        AddTrackItem(crop, "Reset Crop", () => _viewModel.ResetClipCrop(clip), tip: "Nothing of this clip's picture is cut off any more (one step of Undo).");
        menu.Items.Add(crop);

        AddSeparator(menu);
        AddTrackItem(menu, "Fit to Frame", () => _viewModel.FitClipToFrame(clip, fill: false),
            tip: "Sizes the clip so that the whole picture shows, touching the frame on one side, and puts its middle on the frame's.");
        AddTrackItem(menu, "Fill Frame", () => _viewModel.FitClipToFrame(clip, fill: true),
            tip: "Sizes the clip to cover the whole frame, what reaches past it off its edges, and puts its middle on the frame's.");
        AddTrackItem(menu, "Center in Frame", () => _viewModel.CenterClipOnFrame(clip), tip: "Puts the clip's middle on the frame's; its size stays.");
        AddTrackItem(menu, "Reset Transform", () => _viewModel.ResetClipTransform(clip), tip: "This clip back as a new clip starts: sized to the frame, in its middle, not turned, nothing cropped.");

        if (clipsOnTrack > 1)
        {
            AddTrackItem(menu, "Apply Transform to Track", () => _viewModel.ApplyPlacementToTrack(clip),
                tip: $"Gives every other clip on this track the place of {clip.Name}: its corner, size and shape, its turn and anchor, and its crop. Their looks, timing and keyframes stay theirs.");
        }

        return menu;
    }

    /// <summary>Inline numeric menu field: commits on Enter or focus loss as one Undo step; the menu stays open.</summary>
    private void AddNumberRow(ItemsControl menu, string label, Func<double> read, Action<double> write, string step)
    {
        static string Shown(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        var box = new TextBox { Text = Shown(read()), Width = 64, Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        void Take()
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && Math.Abs(value - read()) > 1e-6)
            {
                RunGuarded(step, () =>
                {
                    _viewModel.Checkpoint(step);
                    write(value);
                });
                ScheduleClipRedraw();
            }

            box.Text = Shown(read());
        }

        box.LostKeyboardFocus += (_, _) => Take();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Take();
                box.SelectAll();
                e.Handled = true;
            }
        };

        var line = new DockPanel { LastChildFill = false };
        line.Children.Add(new TextBlock { Text = label, Width = 96, VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(box);
        menu.Items.Add(new MenuItem { Header = line, StaysOpenOnClick = true });
    }

    /// <summary>Apply Game Preset submenu: applies to this clip (or the multi-selection), each clip getting its own Cutouts over its span.</summary>
    private MenuItem GamePresetMenu(Layer clip)
    {
        _viewModel.RefreshStylePresets();
        var menu = new MenuItem { Header = "Apply Game Preset" };
        foreach (var name in _viewModel.StylePresets)
        {
            AddTrackItem(menu, name, () =>
            {
                _viewModel.ApplyGamePreset(GroupHolds && _viewModel.IsInClipGroup(clip) ? _viewModel.SelectedClips.ToList() : [clip], name);
            });
        }

        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "None Saved Yet: Make One in the Cutout Studio", IsEnabled = false });
        return menu;
    }

    /// <summary>Save Game Preset, and import/export .hpstyle files.</summary>
    private MenuItem ManageGamePresetsItem()
    {
        var item = new MenuItem
        {
            Header = "Save, Import or Export Game Presets...",
            ToolTip = "Game Presets: save the layout, Cutouts, color, blur and subtitle settings as one, or load a .hpstyle file (you choose which of its parts to take), or save one to a file.",
        };
        item.Click += (_, e) => ManageStyles_Click(UnifiedTimeline, e);
        return item;
    }

    /// <summary>
    /// Track Preset items: every saved preset (current one ticked) then Clear Track Preset; flat for the header badge, or as a Set Track
    /// Preset submenu plus Clear in the context menu. Each is one Undo step.
    /// </summary>
    private void AddTrackPresetItems(ItemsControl menu, Layer layer, bool flat)
    {
        if (!_viewModel.CanHaveTrackPreset(layer))
            return;

        _viewModel.RefreshStylePresets();
        var current = _viewModel.TrackPresetOf(layer);
        var list = flat ? menu : new MenuItem { Header = "Set Track Preset" };
        foreach (var name in _viewModel.StylePresets)
        {
            var item = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = string.Equals(name, current, StringComparison.Ordinal),
                ToolTip = $"Every video on this track takes {name} now, and every video dropped or moved onto it later, each with Cutouts of its own.",
            };
            item.Click += (_, _) =>
            {
                RunGuarded("Set Track Preset", () => _viewModel.SetTrackPreset(layer, name));
                ScheduleClipRedraw();
            };
            list.Items.Add(item);
        }

        if (list.Items.Count == 0)
            list.Items.Add(new MenuItem { Header = "None Saved Yet: Make One in the Cutout Studio", IsEnabled = false });
        if (!flat)
            menu.Items.Add(list);
        else
            AddSeparator(menu);

        AddTrackItem(menu, "Clear Track Preset", () => _viewModel.SetTrackPreset(layer, null), current is not null,
            tip: "This track gives no Game Preset to what comes onto it; its clips keep the presets they have.");
    }

    /// <summary>Selects the clip and shows the Effects pane.</summary>
    private void ShowInEffectsPane(Layer clip)
    {
        SelectForMenu(clip);
        _viewModel.ShowEffectsPane = true;
    }

    /// <summary>Selects the blur fill row so its settings show in the Effects pane (no separate dialog).</summary>
    private void ShowBlurFillSettings()
    {
        SelectClip(_viewModel.BackgroundLayer);
        _viewModel.ShowEffectsPane = true;
    }

    /// <summary>
    /// Multi-selection items: count header, Delete, Ripple Delete, Mute/Unmute audio (each one Undo step). Paste Effects for all lives
    /// in the Effects pane (MainViewModel.PasteEffectsToClips).
    /// </summary>
    private void AddGroupItems(ItemsControl menu)
    {
        var group = _viewModel.SelectedClips.ToList();
        var count = group.Count.ToString(CultureInfo.InvariantCulture);
        menu.Items.Add(new MenuItem { Header = $"{count} Clips Selected", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        AddTrackItem(menu, $"Delete {count} Clips", DeleteSelectedClip, gesture: "edit.delete",
            tip: "Every selected clip goes into the recycle bin, their time left empty. One step of Undo.");
        AddTrackItem(menu, $"Ripple Delete {count} Clips", () =>
        {
            if (_viewModel.DeleteClips(group, ripple: true))
                ClearClipSelection();
        }, gesture: "edit.rippleDelete", tip: "Every selected clip goes into the recycle bin, and what follows each closes up. One step of Undo.");

        if (group.Exists(c => _viewModel.ClipOf(c) is { HasSound: true }))
        {
            var muted = _viewModel.AreClipsMuted(group);
            AddTrackItem(menu, muted ? $"Unmute {count} Clips" : $"Mute {count} Clips", () => _viewModel.SetClipsMuted(group, !muted),
                tip: muted ? "The sound of every selected clip is heard again." : "The sound of every selected clip is left out of the mix; their pictures stay.");
        }

        AddTrackItem(menu, "Select Only This Clip", () => SelectClip(_selectedClip),
            tip: "Lets go of the others: only the clip right-clicked stays selected.");
    }

    /// <summary>Delete Clip: the clip, or the whole multi-selection when it is a member.</summary>
    private void DeleteMenuClip(Layer clip)
    {
        if (GroupHolds && _viewModel.IsInClipGroup(clip))
        {
            DeleteSelectedClip();
            return;
        }

        _viewModel.DeleteClip(clip);
    }


    /// <summary>
    /// Adds a menu item whose action cancels any active drag, runs guarded (faults become a status line), then rebuilds the timeline.
    /// Shows the bound shortcut when one exists.
    /// </summary>
    private MenuItem AddTrackItem(ItemsControl menu, string header, Action action, bool enabled = true, string? tip = null, string? gesture = null)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, ToolTip = tip };
        if (gesture is not null)
            item.InputGestureText = KeybindManager.GestureText(gesture);
        if (tip is not null)
            ToolTipService.SetShowOnDisabled(item, true);
        item.Click += (_, _) =>
        {
            LetGoOfDrag();
            RunGuarded(header, action);
            ScheduleClipRedraw();
        };
        menu.Items.Add(item);
        return item;
    }

    /// <summary>Adds a separator between groups (never leading, never doubled).</summary>
    private static void AddSeparator(ItemsControl menu)
    {
        if (menu.Items.Count > 0 && menu.Items[^1] is not Separator)
            menu.Items.Add(new Separator());
    }

    /// <summary>Removes leading, trailing and doubled separators from the added range onward.</summary>
    private static void TidySeparators(ItemsControl menu, int first)
    {
        var items = menu.Items;
        for (var i = items.Count - 1; i >= Math.Max(first, 0); i--)
        {
            if (items[i] is Separator && (i == 0 || i == items.Count - 1 || items[i - 1] is Separator))
                items.RemoveAt(i);
        }
    }

    /// <summary>
    /// Selects the menu's target block for selection-based tools (right-click already selects; this covers keyboard-opened menus).
    /// </summary>
    private void SelectForMenu(object clip)
    {
        if (!Equals(_selectedClip, clip))
            SelectClip(clip);
    }

    // Same, but a multi-selection member keeps the group selected (clipboard items act on all).
    private void SelectWithGroup(Layer clip)
    {
        if (!(GroupHolds && _viewModel.IsInClipGroup(clip)))
            SelectForMenu(clip);
    }

    /// <summary>
    /// Add Effect submenu: every applicable effect (Layer.CanTake) by category, added with visible defaults via MainViewModel.AddEffect
    /// (as the Effects pane does), then the clip is selected so the pane shows it. Null when none apply.
    /// </summary>
    private MenuItem? AddEffectMenu(Layer target)
    {
        var menu = new MenuItem { Header = "Add Effect", ToolTip = "Puts an effect on this clip, already set to show (the Effects pane then shows it, to be tuned)." };
        string? category = null;
        foreach (var definition in Models.Effects.EffectRegistry.All.Where(d => target.CanTake(d)).OrderBy(d => Models.Effects.EffectRegistry.CategoryRank(d.Category)))
        {
            if (category is not null && definition.Category != category)
                menu.Items.Add(new Separator());
            category = definition.Category;
            var item = new MenuItem { Header = definition.Name, ToolTip = definition.Description };
            item.Click += (_, _) => RunGuarded($"add {definition.Name}", () =>
            {
                if (_viewModel.AddEffect(target, definition) is null)
                    return;
                SelectClip(target);
                ScheduleClipRedraw();

                // Mask effects open the mask editor, as in the Effects pane.
                if (definition.Type == Models.Effects.EffectRegistry.Mask && !target.CustomMask)
                    OpenLayerTool(target, LayerTool.Mask);
            });
            menu.Items.Add(item);
        }

        return menu.Items.Count > 0 ? menu : null;
    }

    /// <summary>
    /// Toggle A/V Link for a video-with-audio clip (same as U): linked audio moves and trims with the video; unlinked moves independently
    /// (slips, J/L cuts).
    /// </summary>
    private void AddLinkItem(ItemsControl menu, Layer clip, bool linked) =>
        AddTrackItem(menu, "Toggle A/V Link", () =>
        {
            SelectForMenu(clip);
            ToggleSelectedClipLink();
        }, gesture: "clip.toggleLink", tip: linked
            ? "Its sound moves and trims with its picture now. Unlinked, the sound can be moved or trimmed on its own."
            : "Its sound moves on its own now. Linked again, it moves and trims with its picture.");

    /// <summary>
    /// Unlink from Parent, when the track is nested in another's mini-stack (e.g. a HUD Cutout under its video) or the clip overlaps a
    /// parent clip: parent edits stop carrying it. Un-nesting a track takes its clips.
    /// </summary>
    /// <param name="clip">Menu target clip (an empty track's stand-in for empty tracks).</param>
    /// <param name="row">View representing the track: its first clip's, or the empty-track stand-in.</param>
    private void AddUnlinkItem(ItemsControl menu, Layer clip, Layer row)
    {
        var parentRow = _viewModel.ParentRowOf(clip);
        if (parentRow is null && !_viewModel.HasParentClip(clip))
            return;

        AddTrackItem(menu, "Unlink from Parent", () => _viewModel.UnlinkFromParent(parentRow is null ? clip : row), tip: parentRow is null
            ? "Takes this clip out of the keeping of the clip it lies over: splitting, trimming, moving or deleting that clip no longer carries it along."
            : $"Takes this track out of the mini-stack of {parentRow.Name}, its clips with it: edits there no longer carry them along.");
    }

    /// <summary>
    /// Captions submenu for an audio item, filled like the caption row's (FillCaptionMenu), plus Use for Auto-Captions after Generate
    /// Auto-Captions... when the item can be the caption source.
    /// </summary>
    /// <param name="target">The audible clip, or null for the current caption source (ListenedSound).</param>
    private void AddCaptionsSubmenu(ItemsControl menu, object? target, Action? useFor)
    {
        var captions = new MenuItem { Header = "Captions" };
        FillCaptionMenu(captions, target);
        if (useFor is not null)
        {
            var use = new MenuItem
            {
                Header = "Use for Auto-Captions",
                ToolTip = "Auto-Captions listen to this sound from now on: when the video is exported, or when Generate Auto-Captions... is chosen.",
            };
            use.Click += (_, _) => RunGuarded("Use for Auto-Captions", useFor);
            captions.Items.Insert(Math.Min(1, captions.Items.Count), use);
        }

        menu.Items.Add(captions);
    }

    private static readonly double[] GainSteps = [12, 6, 3, 0, -3, -6, -12];

    /// <summary>Gain submenu: common values with the current one ticked; other values via the Gain box.</summary>
    private MenuItem GainMenu(double gainDb, Action<double> setGain)
    {
        var gain = new MenuItem { Header = string.Create(CultureInfo.InvariantCulture, $"Gain ({gainDb:+0.0;-0.0;0} dB)") };
        foreach (var step in GainSteps)
        {
            var db = step;
            var item = new MenuItem
            {
                Header = string.Create(CultureInfo.InvariantCulture, $"{db:+0;-0;0} dB{(db == 0 ? "  (As Recorded)" : "")}"),
                IsCheckable = true,
                IsChecked = Math.Abs(gainDb - db) < 0.05,
            };
            item.Click += (_, _) =>
            {
                RunGuarded("a gain", () => setGain(db));
                ScheduleClipRedraw();
            };
            gain.Items.Add(item);
        }

        return gain;
    }

    /// <summary>
    /// Opens the audio filters dialog for an audio or audible video track, like its Filters button (TrackFilters_Click reads the target
    /// from the sender's DataContext).
    /// </summary>
    private void OpenAudioFilters(object sound) => TrackFilters_Click(new FrameworkElement { DataContext = sound }, new RoutedEventArgs());

    /// <summary>Inline rename that opens one Undo step at the first keystroke (like marker renames).</summary>
    private Action<string> RenameWithUndo(string name, Action<string> rename)
    {
        var started = false;
        return typed =>
        {
            if (!started)
            {
                _viewModel.Checkpoint($"rename {name}");
                started = true;
            }

            rename(typed);
        };
    }

    /// <summary>Ducking checkboxes: duck under voice, or mark as the voice.</summary>
    private void AddDuckingItems(ItemsControl menu, object sound, bool ducks, bool isVoice)
    {
        foreach (var (header, isOn, voice, tip) in new[]
                 {
                     ("Auto-Duck against Voice", ducks, false, "Turns this sound down, by the Duck By (dB) amount in the Audio settings, whenever a sound marked as a voice is speaking."),
                     ("Voiceover / Dialogue", isVoice, true, "This sound is a voice: the sounds set to Auto-Duck make room for it."),
                 })
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isOn, ToolTip = tip };
            item.Click += (_, _) => RunGuarded(header, () => _viewModel.ToggleDucking(sound, voice));
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// Disabled Remove Dead Air for silent video, with a hint to run it on the source video instead (whose cuts reach every unlocked track,
    /// Cutouts included).
    /// </summary>
    private static MenuItem DeadAirWithoutSound(Layer layer)
    {
        var item = new MenuItem
        {
            Header = "Remove Dead Air...",
            IsEnabled = false,
            ToolTip = $"{layer.Name} has no sound of its own to listen to. Run Remove Dead Air on the video it is cut from (or one with sound): its silences come out of every unlocked track at once, this one with it.",
        };
        ToolTipService.SetShowOnDisabled(item, true);
        return item;
    }

    /// <summary>Fade In and Fade Out submenus with the fade-handle menu items (FadeMenuItems).</summary>
    private void AddFadeSubmenus(ItemsControl menu, object clip)
    {
        var (fadeIn, fadeOut) = (FadeMenuItems(clip, fadeIn: true), FadeMenuItems(clip, fadeIn: false));
        if (fadeIn.Count == 0 && fadeOut.Count == 0)
            return;

        if (menu.Items.Count > 0 && menu.Items[^1] is not Separator)
            menu.Items.Add(new Separator());
        foreach (var (header, items) in new[] { ("Fade In", fadeIn), ("Fade Out", fadeOut) })
        {
            var submenu = new MenuItem { Header = header };
            foreach (var item in items)
                submenu.Items.Add(item);
            menu.Items.Add(submenu);
        }
    }

    /// <summary>
    /// Fade menu items: transition styles (current ticked; none for audio; resting on one while paused previews it, PausedHoverPreview),
    /// default-length fade (Settings) or none, and a crossfade over the overlap with any clip on another track spanning that end.
    /// </summary>
    private List<object> FadeMenuItems(object? clip, bool fadeIn)
    {
        var items = new List<object>();
        double seconds;
        TransitionStyle style;
        Action<TransitionStyle> setStyle;
        Action<double> setLength;
        Layer? layer = null;
        switch (clip)
        {
            case Layer { IsLoading: false } own when _viewModel.Layers.Contains(own):
                layer = own;
                (seconds, style) = fadeIn ? (own.FadeInSeconds, own.FadeInStyle) : (own.FadeOutSeconds, own.FadeOutStyle);
                setStyle = chosen => _viewModel.SetClipFadeStyle(own, fadeIn, chosen);
                setLength = length => _viewModel.SetClipFade(own, fadeIn, length);
                break;

            default:
                return items;
        }

        MenuItem Item(string header, Action action, bool ticked = false, string? tip = null)
        {
            var item = new MenuItem { Header = header, IsCheckable = ticked, IsChecked = ticked, ToolTip = tip };
            item.Click += (_, _) =>
            {
                // The choice's own step starts from the clip as it was, not from its hover preview.
                HoverPreview.Leave();
                RunGuarded(header, action);
                ScheduleClipRedraw();
            };
            return item;
        }

        var name = fadeIn ? "In" : "Out";
        items.Add(new MenuItem
        {
            Header = seconds > 0.005 ? string.Create(CultureInfo.InvariantCulture, $"Fade {name}: {seconds:0.##} s") : $"No Fade {name}",
            IsEnabled = false,
        });

        // Audio fades have no style; only video transitions do.
        if (layer is not { IsAudio: true })
        {
            foreach (var each in TransitionStyles.All)
            {
                var chosen = each;
                var styleItem = Item(TransitionStyles.Label(chosen), () => setStyle(chosen), ticked: seconds > 0.005 && chosen == style,
                    tip: seconds > 0.005 ? null : $"Adds a fade {name.ToLowerInvariant()} of the default transition length (Settings > Timeline & Player), in this style.");
                styleItem.Icon = TransitionIcon(chosen, styleItem);
                styleItem.InputGestureText = EaseLabel(TransitionStyles.Easing(chosen));

                // Live paused hover preview: resting on a style shows it in the fade (the playhead moved into it); leaving, or the menu
                // closing, puts the fade back.
                styleItem.MouseEnter += (_, _) => HoverPreview.HoverFade([layer!], fadeIn, chosen);
                styleItem.MouseLeave += (_, _) => HoverPreview.Leave();
                styleItem.Unloaded += (_, _) => HoverPreview.Leave();
                items.Add(styleItem);
            }

            items.Add(new Separator());
        }

        var standard = AppSettings.Current.DefaultTransitionSeconds;
        if (seconds > 0.005)
            items.Add(Item($"Remove Fade {name}", () => setLength(0)));
        else
            items.Add(Item(string.Create(CultureInfo.InvariantCulture, $"Add Fade {name} ({standard:0.##} s)"), () => setLength(standard)));

        if (layer is not null && _viewModel.OverlapAt(layer, fadeIn) is var (other, overlap))
        {
            items.Add(Item(string.Create(CultureInfo.InvariantCulture, $"Crossfade with Overlapping Clip: {other.Name} ({overlap:0.##} s)"),
                () => _viewModel.CrossfadeWithOverlap(layer, fadeIn),
                tip: $"Fades this clip {name.ToLowerInvariant()} and {other.Name} {(fadeIn ? "out" : "in")} over the stretch where both are on, on their separate tracks: the one above dissolves (or wipes, slides...) over the one below, and their sounds cross."));
        }

        return items;
    }

    /// <summary>
    /// Transition style icon (<see cref="TransitionStyles.IconGeometry"/>) stroked and filled with the item's Foreground so it follows
    /// highlight. Bound directly to the item: a FindAncestor binding would resolve before the icon joins the item's tree, fail, and never
    /// retry.
    /// </summary>
    private static System.Windows.Shapes.Path TransitionIcon(TransitionStyle style, MenuItem item)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = IconGeometry.Of(style),
            Width = IconGeometry.Width,
            Height = IconGeometry.Height,
            StrokeThickness = 1.1,
            StrokeLineJoin = PenLineJoin.Round,
            SnapsToDevicePixels = true,
        };
        var colour = new System.Windows.Data.Binding(nameof(Foreground)) { Source = item };
        icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, colour);
        icon.SetBinding(System.Windows.Shapes.Shape.FillProperty, colour);
        return icon;
    }

    /// <summary>
    /// Inserts an inline name box into a menu (top, or at <paramref name="at"/>) for audio (output track title or clip name) or a marker.
    /// Each keystroke renames; Enter or Escape closes the menu.
    /// </summary>
    private static void AddRenameItem(ItemsControl menu, string name, Action<string> rename, int at = 0)
    {
        var box = new TextBox { Text = name, MinWidth = 190, Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
        box.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(box.Text))
                rename(box.Text.Trim());
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape && menu is ContextMenu open)
                open.IsOpen = false;
        };

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        line.Children.Add(box);
        at = Math.Clamp(at, 0, menu.Items.Count);
        menu.Items.Insert(at, new MenuItem { Header = line, StaysOpenOnClick = true, ToolTip = "The title a track of the video is saved under in the output file, or what a clip is called." });
        if (menu.Items.Count > at + 1 && menu.Items[at + 1] is not Separator)
            menu.Items.Insert(at + 1, new Separator());
    }

    /// <summary>Opens a clip's mask dialog (Draw Custom Shape uses the Cutout Studio pen).</summary>
    private void OpenMaskTool(Layer clip) => OpenLayerTool(clip, LayerTool.Mask);

    /// <summary>Opens a track modifier dialog; its changes undo as one step.</summary>
    private void OpenLayerTool(Layer layer, LayerTool tool)
    {
        _viewModel.Checkpoint(tool == LayerTool.Filters ? $"change the filters of {layer.Name}" : $"change the mask of {layer.Name}");

        var visit = _viewModel.UndoMark;
        var dialog = new LayerToolDialog(layer, _viewModel, tool) { Owner = this };
        dialog.ShowDialog();
        _viewModel.DropStepIfUnchanged(visit);
        if (dialog.RedrawRequested)
            _viewModel.DrawTargetCommand.Execute(layer);
    }

    // ----- Region-aware menus and live looping hover previews (Pass 5.7.1 lane L2) -----
    // UnifiedTimelineControl names the part of a clip a right-click lands on (TimelineMenuRequest.Region); FillRegionMenu fills that part's
    // menu: Fade & Ease on a fade, Crossfade / curve / Roll / Heal on a cut seam, Gain / Auto-Level / Ducking on the gain line or waveform,
    // and the slim core clip menu on the body. Resting on a fade style or length, a crossfade length or curve, a gain, Auto-Level or Auto-Duck
    // loops the real GPU composite (and its sound) round its target (PausedHoverPreview with the engine as its Loop host, pre-warmed as the
    // menu opens); leaving, closing, choosing or playing puts the clip and the parked playhead back, with no Undo step and nothing dirty.

    /// <summary>Which menu a timeline right-click fills.</summary>
    internal enum RegionMenu
    {
        /// <summary>The track menu as before (header, effect lanes, captions): FillTrackMenu.</summary>
        Track,

        /// <summary>The slim core clip menu (about a dozen items, the rest in submenus).</summary>
        Core,

        /// <summary>The Fade &amp; Ease menu of the clip's fade in or out.</summary>
        FadeEase,

        /// <summary>The Seam menu: crossfade, its curve, roll and heal.</summary>
        Seam,

        /// <summary>The Audio &amp; Gain menu: gain, Auto-Level -14 LUFS, ducking.</summary>
        Gain,
    }

    /// <summary>Common fade and crossfade lengths offered with live previews (s).</summary>
    internal static double[] RegionLengths => [0.25, 0.5, 1, 2];

    /// <summary>The menu a request fills, from its region, header flag, row data, block clip and seam (pure; goldens).</summary>
    internal static RegionMenu MenuKindOf(ClipMenuRegion region, bool onHeader, object? rowData, object? clip, bool hasSeam)
    {
        if (onHeader || rowData is not Layer { IsCaptions: false, IsEmptyTrack: false })
            return RegionMenu.Track;
        if (region == ClipMenuRegion.Seam)
            return hasSeam ? RegionMenu.Seam : clip is Layer or LayerSound ? RegionMenu.Core : RegionMenu.Track;
        if (clip is not (Layer or LayerSound))
            return RegionMenu.Track;
        return region switch
        {
            ClipMenuRegion.FadeIn or ClipMenuRegion.FadeOut => RegionMenu.FadeEase,
            ClipMenuRegion.Gain => RegionMenu.Gain,
            _ => RegionMenu.Core,
        };
    }

    /// <summary>What an ease is called on a fade style's item.</summary>
    internal static string EaseLabel(EasingType easing) => easing switch
    {
        EasingType.Linear => "Linear",
        EasingType.EaseIn => "Ease In",
        EasingType.EaseOut => "Ease Out",
        EasingType.Back => "Overshoot",
        EasingType.Smoothstep => "Smooth",
        _ => "Ease In-Out",
    };

    partial void FillRegionMenu(TimelineMenuRequest request, ref bool filled)
    {
        var kind = MenuKindOf(request.Region, request.OnHeader, request.Row?.Data, request.Block?.Clip, request.Seam is not null);
        if (kind == RegionMenu.Track || request.Row?.Data is not Layer row)
            return;

        var menu = request.Menu;
        var first = menu.Items.Count;
        var hover = HoverPreview;
        hover.Loop ??= new EngineLoopHost(this);
        hover.Leave();
        var clip = request.Block?.Clip switch
        {
            Layer layer => layer,
            LayerSound sound => sound.Layer,
            _ => null,
        };

        (double From, double To)? warm = null;
        switch (kind)
        {
            case RegionMenu.FadeEase when clip is not null:
                warm = FillFadeEaseMenu(menu, clip, request.Region == ClipMenuRegion.FadeIn);
                break;
            case RegionMenu.Seam when request.Seam is { } seam:
                warm = FillSeamMenu(menu, seam);
                break;
            case RegionMenu.Gain when clip is not null:
                warm = FillGainMenu(menu, row, clip);
                break;
            case RegionMenu.Core:
                FillLayerMenu(menu, row, request.Block?.Clip, slim: true);
                break;
        }

        TidySeparators(menu, first);
        if (menu.Items.Count == first)
            return;

        filled = true;
        menu.Closed += (_, _) => hover.MenuClosed();
        if (warm is var (from, to))
            hover.Warm(from, to);
    }

    /// <summary>
    /// A menu item whose hover shows a live preview (<paramref name="preview"/>, after the dwell) and whose click first puts the preview
    /// back, then runs <paramref name="commit"/> as its own step.
    /// </summary>
    private MenuItem PreviewItem(ItemsControl menu, string header, Action commit, Action? preview, bool ticked = false, bool enabled = true, string? tip = null,
        string? keys = null)
    {
        var item = new MenuItem { Header = header, IsCheckable = ticked, IsChecked = ticked, IsEnabled = enabled, ToolTip = tip, InputGestureText = keys ?? "" };
        item.Click += (_, _) =>
        {
            HoverPreview.Leave();
            LetGoOfDrag();
            RunGuarded(header, commit);
            ScheduleClipRedraw();
        };
        if (preview is not null)
        {
            item.MouseEnter += (_, _) => preview();
            item.MouseLeave += (_, _) => HoverPreview.Leave();
            item.Unloaded += (_, _) => HoverPreview.Leave();
        }

        menu.Items.Add(item);
        return item;
    }

    private static void AddTitle(ItemsControl menu, string title) => menu.Items.Add(new MenuItem { Header = title, IsEnabled = false });

    /// <summary>
    /// Fade &amp; Ease menu (a fade handle, wedge or curve): the fade's length and style (each style showing its ease), add or remove, and a
    /// crossfade with an overlapping clip; lengths and styles preview live. Returns the window to pre-warm.
    /// </summary>
    private (double From, double To)? FillFadeEaseMenu(ItemsControl menu, Layer clip, bool fadeIn)
    {
        if (FadeMenuItems(clip, fadeIn) is not { Count: > 0 } items || _viewModel.ClipOf(clip) is not { } model)
            return null;

        var (seconds, style) = fadeIn ? (model.FadeInSeconds, clip.FadeInStyle) : (model.FadeOutSeconds, clip.FadeOutStyle);
        var name = fadeIn ? "In" : "Out";
        AddTitle(menu, seconds > 0.005 && !clip.IsAudio
            ? string.Create(CultureInfo.InvariantCulture, $"Fade {name}: {seconds:0.##} s, {EaseLabel(TransitionStyles.Easing(style))}")
            : seconds > 0.005 ? string.Create(CultureInfo.InvariantCulture, $"Fade {name}: {seconds:0.##} s") : $"No Fade {name}");

        // Length: common values (ticked when current), each previewed live.
        var lengths = new MenuItem { Header = "Length" };
        var other = fadeIn ? model.FadeOutSeconds : model.FadeInSeconds;
        foreach (var length in RegionLengths.Where(l => l <= model.Duration - other + 1e-6))
        {
            var each = length;
            PreviewItem(lengths, string.Create(CultureInfo.InvariantCulture, $"{each:0.##} s"), () => _viewModel.SetClipFade(clip, fadeIn, each),
                () => HoverPreview.HoverFadeLength([clip], fadeIn, each), ticked: Math.Abs(seconds - each) < 0.005);
        }

        if (lengths.Items.Count > 0)
            menu.Items.Add(lengths);
        AddSeparator(menu);

        // The fade items without their title (styles, add or remove, crossfade with an overlap).
        foreach (var item in items.Skip(1))
            menu.Items.Add(item);

        var fade = seconds > 0.005 ? seconds : Math.Min(AppSettings.Current.DefaultTransitionSeconds, model.Duration);
        return fadeIn ? (model.Start, model.Start + fade) : (model.End - fade, model.End);
    }

    /// <summary>
    /// Seam menu (a cut between two clips on one track): crossfade (Ctrl+T) and its length, the audio curve (equal power or linear), roll
    /// the cut a frame or to the playhead, heal a through-edit (Delete). Lengths and curves preview live. Returns the window to pre-warm.
    /// </summary>
    private (double From, double To)? FillSeamMenu(ItemsControl menu, TimelineSeamTarget seam)
    {
        if (LayerOfBlock(seam.Left) is not { } leftView || LayerOfBlock(seam.Right) is not { } rightView
            || LiveClip(leftView, quiet: true) is not { } left || LiveClip(rightView, quiet: true) is not { } right
            || _viewModel.ClipOf(left) is not { } outgoing || _viewModel.ClipOf(right) is not { } incoming)
        {
            return null;
        }

        var crossfade = incoming.TransitionIn;
        var standard = Math.Max(AppSettings.Current.DefaultTransitionSeconds, 0.04);
        var through = _viewModel.IsThroughEdit(left, right);
        AddTitle(menu, crossfade > 0.01 ? string.Create(CultureInfo.InvariantCulture, $"Crossfade: {crossfade:0.##} s") : through ? "Cut: Through-Edit" : "Cut");

        // Crossfade: add or remove, and its length.
        if (crossfade > 0.01)
            PreviewItem(menu, "Remove Crossfade", () => _viewModel.SetCrossfade(right, 0), () => HoverPreview.HoverSeam(left, right, 0, null));
        else
            PreviewItem(menu, string.Create(CultureInfo.InvariantCulture, $"Add Crossfade ({standard:0.##} s)"), () => CrossfadeSeam(seam, standard),
                () => HoverPreview.HoverSeam(left, right, standard, null), keys: "Ctrl+T");

        var lengths = new MenuItem { Header = "Crossfade Length" };
        foreach (var length in RegionLengths.Where(l => l <= Math.Min(outgoing.Duration, incoming.Duration) + 1e-6))
        {
            var each = length;
            PreviewItem(lengths, string.Create(CultureInfo.InvariantCulture, $"{each:0.##} s"), () => CrossfadeSeam(seam, each),
                () => HoverPreview.HoverSeam(left, right, each, null), ticked: Math.Abs(crossfade - each) < 0.005);
        }

        if (lengths.Items.Count > 0)
            menu.Items.Add(lengths);

        // Audio curve (item 9): equal power for two sounds, linear for one cut in two; hovering hears it (at the default length on a cut).
        var curves = new MenuItem { Header = "Audio Curve" };
        var current = CrossfadeCurves.Known(right.CrossfadeInCurve);
        foreach (var curve in CrossfadeCurves.All)
        {
            var each = curve;
            PreviewItem(curves, CrossfadeCurves.Label(each), () => SetSeamCurve(seam, each),
                () => HoverPreview.HoverSeam(left, right, crossfade > 0.01 ? null : standard, each), ticked: each == current,
                tip: each == CrossfadeCurve.Linear ? "Even level for one take cut in two." : "Even loudness across two different sounds.");
        }

        menu.Items.Add(curves);
        AddSeparator(menu);

        // Roll: a frame either way (comma, period) or to the playhead when it is inside both clips.
        AddTrackItem(menu, "Roll Cut 1 Frame Earlier", () => NudgeSeam(seam, -1)).InputGestureText = ",";
        AddTrackItem(menu, "Roll Cut 1 Frame Later", () => NudgeSeam(seam, 1)).InputGestureText = ".";
        var at = _viewModel.PositionMs / 1000;
        AddTrackItem(menu, "Roll Cut to Playhead", () => _viewModel.RollCut(left, right, at), at > outgoing.Start + 0.01 && at < incoming.End - 0.01 && Math.Abs(at - incoming.Start) > 1e-3);
        AddSeparator(menu);
        AddTrackItem(menu, "Heal Through-Edit", () => HealSeam(seam), through, through ? null : "Only one take cut in two heals.").InputGestureText = "Delete";

        var half = (crossfade > 0.01 ? crossfade : standard) / 2;
        return (incoming.Start - half, incoming.Start + half);
    }

    /// <summary>
    /// Audio &amp; Gain menu (a gain line or waveform): gain steps, Auto-Level -14 LUFS, Auto-Duck and voice, audio filters and the A/V
    /// link. Gains, Auto-Level and Auto-Duck preview live (item 11). Returns the window to pre-warm; null when the clip has no sound.
    /// </summary>
    private (double From, double To)? FillGainMenu(ItemsControl menu, Layer row, Layer clip)
    {
        if (row.IsVideoOnlyChild || clip.IsVideoOnlyChild || _viewModel.ClipOf(clip) is not { } model)
            return null;

        var hasSound = model.HasSound || clip.CarriesSound;
        var linkable = !clip.IsAudio && model.HasSound;
        if (!hasSound && !row.CarriesSound)
            return null;

        AddTitle(menu, string.Create(CultureInfo.InvariantCulture, $"Gain: {clip.GainDb:+0.0;-0.0;0} dB"));
        foreach (var step in GainSteps)
        {
            var db = step;
            PreviewItem(menu, string.Create(CultureInfo.InvariantCulture, $"{db:+0;-0;0} dB{(db == 0 ? "  (As Recorded)" : "")}"), () => _viewModel.SetClipGain(clip, db),
                () => HoverPreview.HoverGain(clip, db), ticked: Math.Abs(clip.GainDb - db) < 0.05);
        }

        AddSeparator(menu);
        if (model.HasSound)
        {
            PreviewItem(menu, "Auto-Level Speech (-14 LUFS)", () => _ = AutoLevelSpeechAsync(clip), () => HoverPreview.HoverAutoLevel(clip, SpeechGainOf(clip)),
                enabled: _viewModel.CanAutoLevelSpeech(clip), tip: "Sets this clip's gain to -14 LUFS speech.");
        }

        if (row.CarriesSound)
        {
            var ducks = row.AutoDuck;
            PreviewItem(menu, "Auto-Duck against Voice", () => _viewModel.ToggleDucking(row, false), () => HoverPreview.HoverDucking(row, clip, !ducks), ticked: ducks,
                tip: "Turns this sound down while a voice speaks.");
            PreviewItem(menu, "Voiceover / Dialogue", () => _viewModel.ToggleDucking(row, true), null, ticked: row.IsVoice, tip: "Marks this sound as a voice to duck under.");
            AddSeparator(menu);
            AddTrackItem(menu, "Audio Filters...", () => OpenAudioFilters(row));
        }

        if (linkable)
            AddLinkItem(menu, clip, model.AudioLinked);

        var playhead = _viewModel.PositionMs / 1000;
        var from = playhead >= model.Start && playhead < model.End ? playhead : model.Start;
        return (from, Math.Min(from + Shell.PausedHoverPreview.SoundLoopSeconds, model.End));
    }

    /// <summary>Auto-Level's gain for a clip, measured in the background (null when silent or soundless); for its hover preview.</summary>
    private Func<Task<double?>> SpeechGainOf(Layer clip) => async () =>
    {
        if (_viewModel.ClipOf(clip) is not { HasSound: true, SourcePath: { Length: > 0 } path } model)
            return null;

        var (stream, from) = (model.AudioStreamIndex, clip.MediaOffset);
        var span = Shell.PausedHoverPreview.MeasuredMediaSpan(clip);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var measured = await Task.Run(() => Services.Audio.SpeechLevel.Measure(path, stream, from, span, limit.Token), limit.Token);
        return Services.Audio.SpeechLevel.GainDb(measured.Lufs, measured.Peak);
    };

    /// <summary>
    /// Golden seam (track-menu-hover-571): a window as far as the region menus reach, its view model and hover-preview holder only (no
    /// constructor run, nothing shown or made).
    /// </summary>
    internal static MainWindow MenuShellForTests(MainViewModel vm)
    {
        var window = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        System.Runtime.CompilerServices.Unsafe.AsRef(in window._viewModel) = vm;
        System.Runtime.CompilerServices.Unsafe.AsRef(in window._hotbarUi) = new HotbarState();
        return window;
    }

    /// <summary>Golden seam: fills <paramref name="request"/>'s menu for its region (FillRegionMenu); true when a region menu took it.</summary>
    internal bool FillRegionMenuForTests(TimelineMenuRequest request)
    {
        var filled = false;
        FillRegionMenu(request, ref filled);
        return filled;
    }

    /// <summary>The GPU preview engine as the hover preview's loop host (whichever engine runs now; none: the playhead moves instead).</summary>
    private sealed class EngineLoopHost(MainWindow window) : Shell.ILoopPreviewHost
    {
        public void Warm(double from, double to) => window._gpu?.WarmLoopPreview(from, to);

        public void EndWarm() => window._gpu?.EndLoopWarm();

        public bool Start(double from, double to) => window._gpu is { } engine && !window._viewModel.IsPlaying && engine.StartLoopPreview(from, to);

        public void Stop(double parkAt, bool keepPlaying) => window._gpu?.StopLoopPreview(parkAt, keepPlaying);
    }
}
