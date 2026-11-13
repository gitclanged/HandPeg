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

// ----- Tracks, continued: a track's right-click menu (FillTrackMenu, FillLayerMenu) and everything it lists -----
public partial class MainWindow
{
    // ----- Right-click on a track -----
    // One menu for every row of the timeline (4.4): the UnifiedTimelineControl asks for it (MenuRequested) with the row, the
    // block pressed (or a fade handle, or the header), and FillTrackMenu fills it with what can be done as things are now.
    // Names follow one scheme: Title Case; "..." only where a window asks for more; the key bound to an action shown beside
    // it (KeybindManager), never in its name; Delete for content, Remove for an attribute, Clear for a collection, Restore
    // to bring something back.

    /// <summary>
    /// Fills a timeline row's right-click menu: what can be done to the track as a whole (its filters, its place, its mask,
    /// its look, its sound), and to the clip that was clicked (split, trim, fades, speed, delete). A fade handle's menu is that
    /// fade's alone. The items are added after any the menu has already.
    /// </summary>
    /// <param name="rowData">The row's Data: a track's row (a Layer: a video, picture or sound track, the caption row, the blur fill's settings row); null for the space below the rows.</param>
    /// <param name="clicked">The pressed block's Clip (a Layer, a LayerSound, a SubtitleRef), or null.</param>
    /// <param name="fade">The fade handle pressed, when one was: its block's clip, and whether it is the fade in.</param>
    /// <param name="onHeader">The row's header was right-clicked, not its time: the menu is about the track (and the clip selected on it).</param>
    internal void FillTrackMenu(ItemsControl menu, object? rowData, object? clicked, (object Clip, bool In)? fade, bool onHeader)
    {
        // A right-click on a fade handle: that fade's menu, and nothing else.
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
    /// A track row's menu (a Layer: a video, picture or sound track, the caption row, the blur fill's settings row), in the
    /// order of an edit (5.1-DogfoodPolish, the user's overhaul): Cut &amp; Edit first and flat, then one submenu for each thing a clip has
    /// (its transform, its Game Preset, its effects, its sound, its time), the Smart Tools flat again, and what is the track's last.
    /// </summary>
    /// <param name="onHeader">Its header was right-clicked: what is the track's (its Track Preset, 5.2A) comes first, not in the Track submenu.</param>
    private void FillLayerMenu(ItemsControl menu, Layer layer, object? clicked, bool onHeader = false)
    {
        // The clip the menu is about: the one clicked, else the one selected on this track, else the track's first.
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

        // One of clips selected together (5.0): what is done to all of them comes first, each one step of Undo.
        if (GroupHolds && _viewModel.IsInClipGroup(clip))
        {
            AddGroupItems(menu);
            Separate();
        }

        var at = _viewModel.PositionMs / 1000;

        // An empty track has nothing to set but its Track Preset (5.2A: set before anything is dropped onto it): it can only leave the
        // mini-stack it is in, or be deleted.
        if (layer.IsEmptyTrack)
        {
            AddTrackPresetItems(menu, layer, flat: false);
            menu.Items.Add(ManageGamePresetsItem());
            Separate();
            AddUnlinkItem(menu, layer, layer);
            Add("Delete Track", () => _viewModel.RemoveTrack(layer));
            return;
        }

        // The header's menu is the track's: its Track Preset first (5.2A).
        if (onHeader && _viewModel.CanHaveTrackPreset(layer))
        {
            AddTrackPresetItems(menu, layer, flat: false);
            menu.Items.Add(ManageGamePresetsItem());
            Separate();
        }

        // A sound's name, typed in place at the top of its menu (one step of Undo, taken at the first keystroke). On a stream's
        // Sub-Lane it is the lane's name, and so the title of the output track the lane goes to (5.2-Arm64AndNpu, the user's call).
        if (layer.IsAudio)
        {
            if (_viewModel.StreamLaneName(layer) is { } laneName)
                AddRenameItem(menu, laneName, RenameWithUndo(laneName, name => _viewModel.RenameStreamLane(layer, name)), menu.Items.Count);
            else
                AddRenameItem(menu, clip.Title, RenameWithUndo(clip.Name, name => clip.Title = name), menu.Items.Count);
            Separate();
        }

        // The caption row: the captions' own menu (making, editing, styling, placing, importing and deleting them).
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

        // The clipboard (5.2A): on this clip, or the clips selected with it.
        if (_viewModel.CanGroup(clip))
        {
            Add("Copy", () => { SelectWithGroup(clip); CopySelectedClips(); }, gesture: "edit.copy");
            Add("Cut", () => { SelectWithGroup(clip); CutSelectedClips(); }, gesture: "edit.cut");
            Add("Duplicate", () => { SelectWithGroup(clip); DuplicateSelectedClips(); }, gesture: "edit.duplicate",
                tip: "A copy of this clip (or the clips selected with it), with its Cutouts, right after it on the same track where there is room.");

            // Onto a track of its own (5.4A): one click, its stream sounds and Cutouts with it, one step of Undo.
            if (!clip.IsAudio)
            {
                var movable = _viewModel.CanMoveClipsToNewTrack([clip]);
                Add("Move to New Track Above", () => { SelectWithGroup(clip); MoveSelectedToNewTrack(above: true); }, movable, gesture: "clip.newTrackAbove",
                    tip: "Puts this clip (or the clips selected with it) on a new track just above its own, with its stream sounds and Cutouts; a clip alone on its track goes above the next track up.");
                Add("Move to New Track Below", () => { SelectWithGroup(clip); MoveSelectedToNewTrack(above: false); }, movable, gesture: "clip.newTrackBelow",
                    tip: "Puts this clip (or the clips selected with it) on a new track just below its own, with its stream sounds and Cutouts; a clip alone on its track goes below the next track down.");
            }

            Add("Paste Attributes...", () => { SelectWithGroup(clip); PasteAttributesToSelection(); }, _viewModel.HasCopiedAttributes, gesture: "edit.pasteAttributes",
                tip: "Gives this clip (or the clips selected with it) the parts you choose of the copied clip's look: its transform, crop, opacity, effects, keyframes, mask, fades or sound.");
        }

        if (_viewModel.HasCopiedClips && layer.HasTiming)
            Add("Paste at Playhead", () => _viewModel.PasteClips(at), gesture: "edit.paste");
        if (editable)
            Add("Duplicate Track", () => _viewModel.DuplicateLayer(layer), tip: "A copy of this whole track, every clip on it, on a new track just above, moved a little on the frame.");

        // A sound on a shared lane, or on any Audio track (5.2): onto a lane of its own under its video's track, or a track of its own.
        if (layer.IsAudio && _viewModel.ClipOf(clip) is not null)
        {
            if (_viewModel.ParentRowOf(layer) is { } videoRow && _viewModel.CanMoveSoundUnder(clip, videoRow))
                Add("Move to Its Own Sub-Lane", () => _viewModel.MoveSoundToOwnLane(clip, videoRow),
                    tip: "Takes this sound off the lane it shares onto a lane of its own in the same track's audio stack, at the same time. Dragging it onto a video track's row does the same there.");
            Add("Move to a New Audio Track", () => _viewModel.MoveSoundToOwnLane(clip, null),
                tip: "Takes this sound onto an Audio track of its own, at the same time; it still follows its video clip until it is unlocked from it.");
        }

        // Silences Split & Mark folded out at its cuts (4.3D): each put back where it was, what follows sliding on; and
        // (4.4 BugHunt) each stretch put back over the clip folded out again, what follows sliding back.
        if (layer.HasTiming)
        {
            foreach (var fold in _viewModel.FoldsAt(clip))
            {
                Add(string.Create(CultureInfo.InvariantCulture, $"Restore Folded Silence ({fold.Length:0.##} s) at Its {(fold.AtEnd ? "End" : "Start")}"),
                    () => AnimatedFoldChange(() => _viewModel.UnfoldSegment(fold)));
            }

            foreach (var restored in _viewModel.RestoredFoldsOn(clip))
            {
                Add(string.Create(CultureInfo.InvariantCulture, $"Hide / Fold Dead Air Segment ({restored.Length:0.##} s)"),
                    () => AnimatedFoldChange(() => _viewModel.HideRestoredStretch(restored)),
                    tip: "Folds out again the stretch of dead air that was put back here: what follows closes up, and its seam can put it back once more.");
            }

            if (_viewModel.FoldCount is > 1 and var folds)
                Add(string.Create(CultureInfo.InvariantCulture, $"Restore All {folds} Folded Silences"), () => AnimatedFoldChange(_viewModel.UnfoldAll));
        }

        // Its own switches (5.2), as on its block: On/Off for this clip alone, and a child's Lock to its parent clip.
        if (_viewModel.ClipOf(clip) is not null && !layer.IsCaptions)
        {
            var on = _viewModel.IsClipOn(clip);
            Add(on ? clip.IsAudio ? "Turn Off (Mute This Clip)" : "Turn Off (Hide This Clip)" : "Turn On", () => _viewModel.SetClipOn(clip, !on),
                tip: "This clip alone: its Cutouts and effects keep their own switches. The track header's switch takes the whole track and its stack.");
            if (_viewModel.ClipLock(clip) is { } locked)
            {
                Add(locked ? "Unlock from Parent Clip" : "Lock to Parent Clip", () => _viewModel.SetClipLocked(clip, !locked),
                    tip: locked ? "Frees it: it is dragged, slid and trimmed on its own, and its clip no longer takes it along."
                        : "Locks it to the clip it lies over: it moves, trims and splits with it.");
            }
        }

        Separate();

        // ----- Transform, Sizing & Position -----
        if (clip is { HasPlacement: true, IsCaptions: false } && _viewModel.ClipOf(clip) is not null)
            menu.Items.Add(TransformMenu(clip, clips.Count));

        // ----- Cutout & Mask Studio (5.2: on every video clip, right on its menu and first in the submenu) -----
        var isVideoClip = clip.IsVideoFile;
        const string StudioTip = "The Cutout Studio on this clip: trace its shape or a game's HUD by hand on a still, zoomed in to the very pixels; "
                                 + "cut this clip to it, add the Cutouts to it, or save them as a Game Preset.";
        if (isVideoClip)
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
            }, EffectsView.HasCopiedEffects);
        }

        if (layer.HasTiming)
        {
            AddSeparator(effects);

            // Its own fades, over whatever is under it (the handles at its top corners), and a crossfade with a clip on
            // another track that it overlaps.
            AddFadeSubmenus(effects, clip);

            // The cuts it shares with the clips before and after it on its track: a crossfade across either.
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
        // How loud the clip is, the track's filters, the clip's link to its picture (J and L cuts, slips), and what the track's sound
        // does when someone is speaking. (None of it on a video-only child, 5.0-Release: a blur fill, a HUD Cutout or one cut by a mask.)
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

        // The beats of the clip's sound (5.2B): found in the background, kept with the clip (one step of Undo); dragged clips snap to them and
        // the beat grid draws them on the clip (both on the Snap button's menu).
        if (!layer.IsVideoOnlyChild && !clip.IsVideoOnlyChild && _viewModel.ClipOf(clip) is { HasSound: true })
        {
            AddSeparator(audio);
            AddTrackItem(audio, "Detect & Snap to Audio Beats", () => _ = DetectBeatsAsync(clip), _viewModel.CanDetectBeats(clip),
                "Finds the beats of this clip's sound (in the background) and keeps them with the clip: dragged clips' edges snap to them, and the beat grid shows them as ticks along the clip. One step of Undo.");
            if (_viewModel.HasBeats(clip))
                AddTrackItem(audio, "Clear Audio Beats", () => _viewModel.ClearClipBeats(clip), true, "Takes this clip's beats away. One step of Undo.");
        }

        // A stream's lane: the output track its stream place goes to (5.2-AuditB: what each output audio track is made as, by its
        // place), kept or left out of the export.
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

        // Every video track is equal (the user's directive): all of its sound on lanes of their own.
        if (editable && clip.IsVideoFile && _viewModel.CanUnpackStreams(clip))
        {
            AddTrackItem(audio, "Unpack Audio Streams", () => _ = _viewModel.UnpackAudioStreamsAsync(clip), true,
                "Gives each audio stream of this file (game, desktop, microphone) a lane of its own in the track's audio stack, with its own mute, gain, filters and voice switch. One step of Undo.");
        }

        Keep(audio);

        // ----- Speed & Time -----
        var time = new MenuItem { Header = "Speed & Time" };

        // How fast the clip runs: a few usual speeds, the one it has ticked. Anything between is typed into its Speed box.
        if (clip.HasSpeed)
        {
            var speeds = new MenuItem { Header = string.Create(CultureInfo.InvariantCulture, $"Clip Speed ({clip.Speed:0.##}x)") };
            foreach (var speed in new[] { 0.1, 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 4, 10 })
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

        // A proxy of the clip's file, on demand (5.0D, see ProxyCache), offered only where it helps (5.0-Verify, the user's Q6):
        // a long-GOP video layer the preview cannot decode fast, a clip that has it on, or Shift held as the menu opens.
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
        // The Personal Clip Library (5.2C): the clip's span kept across projects, a lossless copy.
        if (_viewModel.CanSaveToClipLibrary(clip))
            Add("Save Clip to Personal Clip Library...", () => SaveToClipLibrary(clip), true,
                "Keeps this clip's span (its picture and every sound of its file, a lossless copy: nothing encoded again) in your own library, Documents\\HandPeg\\Clips, with a title, tags and the focal point of a Ping-to-Mark highlight inside it. The Media pane's Clip Library lists it, to search, scrub and drag into any project.");

        // A double-wide recording (5.2C): the gameplay and the facecam as two clips in step, placed for the Sequence's shape.
        if (editable && _viewModel.CanUnpackSideBySide(clip))
            Add("Unpack OBS Side-by-Side Canvas (Gameplay + Facecam)", () => _viewModel.UnpackSideBySide(clip), true,
                "This recording is two pictures side by side on one canvas. The clip keeps the left half (the gameplay), cut to it and placed for the Sequence's shape, and a Cutout of the same file above it shows the right half (the facecam): a corner picture on a wide frame, a band across the top of a tall one. The two stay in step through every split, trim and move. One step of Undo.");

        if (editable && layer is { HasFilters: true, IsAdjustment: false, IsBackground: false })
            Add("3D Fly-In + Shockwave Impact", () => _viewModel.FlyInWithShockwave(clip), _viewModel.HasSource,
                "From the playhead the clip flies out to the middle of the frame, lands with a shockwave and a camera shake, and glides back. It is all keyframes on the clip and an Impact clip: change anything. One step of Undo.");

        if (editable)
        {
            // What its sound is listened to for: the captions (a clip with sound through its own file, a video through its
            // streams on Sub-Lanes) and the silences.
            if (clip.CarriesSound)
            {
                Add("Generate Auto-Captions...", () => GenerateCaptionsFrom(clip), _viewModel.HasSource);
                AddCaptionsSubmenu(menu, clip, () => _viewModel.UseLayerAudioForCaptions(clip));
            }
            else if (clip.IsVideoFile && _viewModel.CaptionStreamsOf(clip) is { Count: > 0 })
            {
                Add("Generate Auto-Captions...", () => GenerateCaptionsFrom(clip), _viewModel.HasSource);
                AddCaptionsSubmenu(menu, clip, null);
            }

            // Remove Dead Air on every track of video or sound (4.3E). A clip with sound of its own is listened to and cut
            // itself; a video whose streams are on Sub-Lanes listens to its first (or the sound marked as the voice) and cuts its
            // file's clips on every unlocked track, their Cutouts among them; a Cutout of a video has no sound
            // of its own to listen to.
            if (clip.CarriesSound)
            {
                Add("Remove Dead Air...", () => RunDeadAir(clip), _viewModel.HasSource,
                    "Finds the silences in this clip's own sound (or the gaps between its captioned words) and cuts them out of it: Split & Mark folds them out of its track (each can be put back from the clip's menu), or they are deleted.");
            }
            else if (clip.IsVideoFile && !_viewModel.IsCutout(clip) && _viewModel.CaptionStreamsOf(clip) is { Count: > 0 })
            {
                Add("Remove Dead Air...", () => RunDeadAir(clip), _viewModel.HasSource,
                    "Finds the silences in this video's sound (the stream marked as the voice, else its first) and cuts them out of every clip of its file on every unlocked track at once, their Cutouts and the other clips with them.");
            }
            else if (clip.IsVideoFile)
            {
                menu.Items.Add(DeadAirWithoutSound(clip));
            }
        }

        Separate();

        // ----- What is the track's -----
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
        if (editable || layer.IsBackground)
            AddTrackItem(track, layer.IsHidden ? "Show" : layer.IsAudio ? "Mute" : "Hide", () => _viewModel.ToggleHidden(layer));
        if (editable && clips.Count > 1)
        {
            AddSeparator(track);
            AddTrackItem(track, "Delete Track", () => _viewModel.RemoveTrack(layer), tip: $"Deletes the whole track, its {clips.Count} clips with it (Undo brings them back).");
        }

        Keep(track);
    }

    /// <summary>
    /// Transform, Sizing &amp; Position: every number of where a clip sits, typed in place, and the one-click placements. The same values the
    /// Timeline's inspector strip and the Canvas set; each change one step of Undo, this clip's alone.
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
            // Yaw / Pitch / Roll (5.2A, true 3D): the Roll is the Rotation; the tilts turn the picture out of the frame, seen in perspective.
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

        // Interactive Crop and Reset Crop (5.4B: from the Video tab's crop cross, which the Export tab no longer has): this clip's.
        // The tick shows the mode itself, whichever clip it was started on, so any clip's menu turns it off (as does Esc).
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

    /// <summary>A number typed in a menu: taken on Enter or when the box is left, as one step of Undo; the menu stays open for the next.</summary>
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

    /// <summary>Apply Game Preset: every saved preset, put on this clip alone (or the clips selected with it), each with Cutouts of its own over its own span.</summary>
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

    /// <summary>Saving a Game Preset and moving one to or from a .hpstyle file (5.4C: on the menus since the Timeline's "..." went).</summary>
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
    /// A track's Game Preset (5.2A, a Track Preset Node): every saved preset, the track's own ticked, then Clear Track Preset. Flat (the
    /// header badge's list), or as a Set Track Preset submenu and the clear beside it (the track's right-click menu). Each one step of Undo.
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

    /// <summary>A clip's effects in the Effects pane: it is selected, and the pane shown.</summary>
    private void ShowInEffectsPane(Layer clip)
    {
        SelectForMenu(clip);
        _viewModel.ShowEffectsPane = true;
    }

    /// <summary>The blur fill's settings row, pinned in the Effects pane, is where it is set (5.1-DogfoodPolish: no dialog of its own).</summary>
    private void ShowBlurFillSettings()
    {
        SelectClip(_viewModel.BackgroundLayer);
        _viewModel.ShowEffectsPane = true;
    }

    /// <summary>
    /// What a menu does to clips selected together (5.0): how many there are, Delete and Ripple Delete, Mute (or Unmute) of
    /// their sound, each one step of Undo. Paste Effects onto all of them is the Effects pane's (MainViewModel.PasteEffectsToClips).
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

    /// <summary>Delete Clip on a clip's menu: the clip, or (one of clips selected together, 5.0) all of them.</summary>
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
    /// Adds an item to a track's menu: what it does (a drag still held let go first; a fault in it a status line, never an
    /// exception that ends the app: 4.4 BugHunt), then the timeline drawn again. A shortcut shows the key bound to its action.
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

    /// <summary>A line between groups of a menu's items: never first, never two together.</summary>
    private static void AddSeparator(ItemsControl menu)
    {
        if (menu.Items.Count > 0 && menu.Items[^1] is not Separator)
            menu.Items.Add(new Separator());
    }

    /// <summary>Takes out the lines a menu's groups left at its ends or two together, from the first item that was added.</summary>
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
    /// Makes the block a menu acts on the selection, for the tools that work on what is selected (a right-click selects its
    /// block already: this keeps the menu right when it was opened another way).
    /// </summary>
    private void SelectForMenu(object clip)
    {
        if (!Equals(_selectedClip, clip))
            SelectClip(clip);
    }

    // The same, but a clip of the clips selected together keeps them selected (the clipboard's items work on all of them, 5.2A).
    private void SelectWithGroup(Layer clip)
    {
        if (!(GroupHolds && _viewModel.IsInClipGroup(clip)))
            SelectForMenu(clip);
    }

    /// <summary>
    /// Add Effect on a track's menu (5.0-Release, the user's Q1): every effect the clip can take (Layer.CanTake), by category,
    /// each put on it with its visible starting values (MainViewModel.AddEffect, as the Effects pane's Add Effect does), the
    /// clip then selected so that the Effects pane shows its new effect; the preview follows the edit as any other. Null when the
    /// clip takes none.
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

                // A mask card needs its mask: the editor opens, as Add Effect in the Effects pane opens it.
                if (definition.Type == Models.Effects.EffectRegistry.Mask && !target.CustomMask)
                    OpenLayerTool(target, LayerTool.Mask);
            });
            menu.Items.Add(item);
        }

        return menu.Items.Count > 0 ? menu : null;
    }

    /// <summary>
    /// Toggle A/V Link for a clip with a picture and a sound (a video file's): linked, its sound moves and
    /// trims with its picture; unlinked, on its own (a slip, J and L cuts). As the U key does, on the clip the menu is about.
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
    /// Unlink from Parent, when the clip's track is in another track's mini-stack (a Game Preset's HUD Cutout under its video's)
    /// or the clip lies over a parent clip: edits of the parent stop carrying it along. A track leaves with its clips.
    /// </summary>
    /// <param name="clip">The clip the menu is about (an empty track's stand-in for an empty track).</param>
    /// <param name="row">The view standing for the clip's track: its first clip's, or the empty track's stand-in.</param>
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
    /// The Captions submenu of a sound's menu, filled as the caption row's is (FillCaptionMenu), with Use for Auto-Captions
    /// after Generate Auto-Captions... when the sound can be chosen as what the captions listen to.
    /// </summary>
    // <param name="target">The sound (a clip with sound), or null for what is set (the sound listened to: ListenedSound).</param>
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

    /// <summary>A sound's Gain submenu: a few usual amounts, the one it has ticked. Anything between is typed into its Gain box.</summary>
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
    /// The audio filters dialog of a sound (a track of sound clips or of a video with sound),
    /// as its Filters button opens it: TrackFilters_Click reads what it is for from its sender's DataContext.
    /// </summary>
    private void OpenAudioFilters(object sound) => TrackFilters_Click(new FrameworkElement { DataContext = sound }, new RoutedEventArgs());

    /// <summary>A rename that takes its one step of Undo at its first keystroke, as a marker's does.</summary>
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

    /// <summary>The two ducking boxes of a sound's right-click menu: turned down under a voice, or the voice itself.</summary>
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
    /// Remove Dead Air on a video with no sound to listen to (4.3E): shown on its menu like every video track's, greyed out, with
    /// where to run it instead (the video it was cut from, whose cuts reach every unlocked track, its Cutouts among them).
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

    /// <summary>A clip's Fade In and Fade Out, each a submenu of what its fade's handle menu offers (see FadeMenuItems).</summary>
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
    /// What a fade's menu offers, for a clip: its transition styles (the one it has ticked;
    /// a sound has none), a fade of the default length (Settings) or none, and, where a clip on another track is on
    /// across that end of the clip, a crossfade with it over their overlap.
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

        // A sound fades in and out; only a picture has a style to come on or go in.
        if (layer is not { IsAudio: true })
        {
            foreach (var each in TransitionStyles.All)
            {
                var chosen = each;
                var styleItem = Item(TransitionStyles.Label(chosen), () => setStyle(chosen), ticked: seconds > 0.005 && chosen == style,
                    tip: seconds > 0.005 ? null : $"Adds a fade {name.ToLowerInvariant()} of the default transition length (Settings > Timeline & Player), in this style.");
                styleItem.Icon = TransitionIcon(chosen, styleItem);
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
    /// A transition style's drawn picture for its menu item (4.2B-3, <see cref="TransitionStyles.IconGeometry"/>), stroked
    /// and filled in the item's own text colour, so it turns with the words when the item is highlighted. The colour is bound to
    /// the item itself: a FindAncestor binding is tried while the icon is not yet in the item's tree, fails, and is never tried again.
    /// </summary>
    private static System.Windows.Shapes.Path TransitionIcon(TransitionStyle style, MenuItem item)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = TransitionStyles.IconGeometry(style),
            Width = EasingCurves.IconWidth,
            Height = EasingCurves.IconHeight,
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
    /// Puts a box for a name in a menu (at the top, or at <paramref name="at"/>): a sound's (the title a track of the video
    /// is saved under, or what a clip is called) or a marker's. Each keystroke renames; Enter or Escape closes the menu.
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

    /// <summary>The shape a clip alone is cut to: its mask dialog, with the Cutout Studio's pen behind Draw Custom Shape.</summary>
    private void OpenMaskTool(Layer clip) => OpenLayerTool(clip, LayerTool.Mask);

    /// <summary>Opens one of a track's modifier dialogs. What it changes can be undone as one step.</summary>
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
}
