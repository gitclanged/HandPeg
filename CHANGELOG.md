# Changelog

Release notes for HandPeg, newest first. Notes for 2.1.0 and earlier are on the
[GitHub Releases page](https://github.com/gitclanged/HandPeg/releases).

## HandPeg v3.0.0

HandPeg 3.0 is rebuilt on 2.5.0, with the parts of 2.6 that worked kept and the heavy machinery behind them left out. It is meant to be a dependable editor for everyday cutting, captioning and vertical HUD layouts.

### Highlights
- **Captions work as they did in 2.5 again.** The Generate Auto-Captions checkbox is back in Editor Mode and is the on/off switch: ticked, the export has captions; cleared, it has none, and nothing stops an export. **Generate Captions** transcribes ahead of time into a subtitle track you can watch and edit; exports and queued jobs then draw that track exactly as it stands and never transcribe it again.
- **HUD masks of the whole frame.** One black-and-white mask of the whole video frame, white over every HUD element, can serve every layer cut from the video: each layer takes its own part of it and stays a layer of its own. Redrawing a layer's area never shifts the mask.
- **Draw Custom Shape.** Trace round a HUD element on a still of the video, point by point; the lines join up, and the shapes become a mask for one layer or for all of them.
- **Mask options:** grow or shrink the mask, invert it, crop any edge of a layer (what is cropped is see-through, the rest stays where it is), and Soft Edges now softens a mask's own outline. A mask can be a PNG with transparency: what is painted shows, what is see-through is cut away.
- **Layers from a Mask:** a mask of the whole frame is taken apart into one layer per HUD element. Patches closer together than a distance you set are one element, so ability icons traced one by one make a single layer. The pieces are shown on a still of the video before they are added.
- **Opacity keyframes:** layers can fade in and out, keyed like position, size and rotation.
- **Built-in HUD presets traced from the games:** Overwatch, Apex Legends, Marvel Rivals and The Finals, each with one mask traced round its HUD and three or four layers, one per section of the HUD (Overwatch: Killfeed, Health & Name, Abilities & Ammo), laid out for vertical video. The other pieces (an ultimate meter, a portrait, the objective) are in the mask too: right-click a HUD layer and choose **Add More Pieces of This Mask** to add them. Counter-Strike 2 is kept as it was.
- **Live audio preview:** every audio track of a video is heard while it plays, not only the first, mixed with the layers' sound, and as the export will have it: silenced stretches, slips, ducking under the voice tracks, each track's gain and filters, and the fades. Changing any of them is heard straight away.
- **Projects and style presets carry their masks**, so one file is all another computer needs.

### New
- Drag-and-drop overlay in place of the launch window: drag a file over an empty window and drop it on a style preset (Editor Mode), an encoding preset (Encoder Mode) or a Social Squisher preset.
- Settings → Automation & Drop Targets: the overlay's style presets and encoding presets are ordered lists, like the Squisher's: move them up and down, remove them, add others, and choose how many are shown.
- Settings window reorganized into seven tabs with search, including an Auto-Captions tab: default language and caption style, beam size, best-of, temperature and fallback, and silence-detection threshold and timings.
- About, Check for Updates and **Update Dependencies** on the General tab of Settings, with a **Manage Dependencies** link to the full list (tool paths, update URLs, speech models) on the Tools & Backup tab.
- When something needs a tool that was never downloaded (Start Encode, Add to Queue, Generate Captions, Edit Captions, loading a web address), it offers to download exactly that.
- Queued jobs transcribe their captions when they run, not when they are added, so adding a job never waits for whisper. A job whose captions are a subtitle track is written at once, as the track stands.
- Startup Window Size: Default Size, Remember Last Size or Launch Maximized.
- A project file (`.hproj`) pasted into the Source box opens the project.
- Undo now covers subtitle edits (in the Subtitle Editor and elsewhere), Generate Captions, importing and removing subtitles, the caption style and caption box, moving and resizing layers in the layout pane, layer properties, Draw Target and applying a style preset.

### Changed
- Scrubbing sends quick seeks at most 30 times a second while dragging and one exact seek on release.
- Settings, presets and the window come up faster: tabs are built when first opened, and the encoder check and dependency update check start after the window is drawn.
- Encoder Mode has no Live Preview and shows no captions in its player.
- Generate Captions asks before replacing a subtitle track that is already there.

### Fixed
- Dropping a video on a style preset in the drag-and-drop overlay closed HandPeg (the player crashed on the HUD masks' soft edge).
- Exports in Editor Mode failing with "press Generate Captions first" when captions were on but nothing had been transcribed (2.6).
- Files dropped over the player being taken by the player instead of HandPeg.
- First-run setup not listing hardware encoders until a restart after choosing an existing FFmpeg.
- The trimmed voiceover not drawn in the Master Mix View.
- The large-v3 and large-v3-turbo speech models showed a 0 MB download size.

### Removed (from 2.6)
- Proxies, Preview Quality, the timeline proxy and its auto-render, and the proxy cache settings: the player shows the original files again.
- Recent Editor Files.

### Compatibility
- Projects from 2.5 and 2.6 open unchanged. Compatibility with older presets and projects is no longer promised where it would hold new features back. Projects saved by unreleased test builds after 2.6.3 (format 3) cannot be opened.
- Projects saved by 3.0 open in 2.5 and 2.6, but without their masks, which those versions cannot unpack.
- Settings and presets carry over. Settings that belonged to proxies are ignored.
- The built-in HUD style presets are written again, replacing earlier copies of the same names.

### Upgrading
Installed copies update automatically.

## HandPeg v2.5.0

### Highlights
- **Text layers.** Titles and labels typed in HandPeg, in any installed font, with color and outline. A text layer is placed, sized, turned, masked and keyframed like any other layer.
- **Subtitle import and a Subtitle Editor.** `.srt` and `.vtt` files are read natively and drawn as captions in the caption style. The new Subtitle Editor shows every subtitle on its own timeline with a close-up preview: drag to retime, drag the dividers between words to time each word, split, add and delete.
- **Social Sharing Squisher.** Drop a video on a preset in the startup dialog and it is squeezed under that size as an H.264 / AAC `.mp4`, then dragged straight out of the window into Discord, a browser or a folder.
- **Lighter, drawn interface.** Time bars, keyframe diamonds and waveforms are drawn as shapes instead of built from controls and bitmaps. Layer and audio time bars zoom and pan together.
- **Unified Layers and Audio tabs.** Both use the same row: name on the left, time bar in the middle, a compact control block on the right that moves into a flyout when the window is narrow.
- **Saving.** Ctrl+S, Save As (Ctrl+Alt+S) and background autosave; none of them touches the undo history.
- **Portable mode.** A `portable.txt` beside the program keeps everything in the program's own folder.

### New
- **Add Layer** menu on the Layers tab: Video, Image, Text, Region of Main Video. **Text Settings** panel for the selected text layer.
- Import External Subtitles: `.srt`, `.vtt`, and image-based `.sup` / `.pgs` / VobSub (`.idx` + `.sub`), which can be retimed but not reworded.
- Subtitle Editor: its own player showing only the captions, a zoomable timeline that scrolls under a fixed playhead, Global Offset (ms), Add Segment, Split, Add Word, Delete, and per-word timing dividers on the selected subtitle.
- **Manual Edit** and **Caption Style Settings** on the caption layer's right-click menu. With no subtitle track yet, Manual Edit makes one from the transcribed captions.
- Whisper **Model** box on the Subtitles tab (tiny to large-v3 and turbo, with `.en` variants); a model that is not downloaded is fetched when chosen.
- Caption generation shows real progress instead of an endless bar.
- The Whisper download picks the best published build for the computer: CUDA with an NVIDIA driver, otherwise OpenBLAS.
- Squisher presets for Discord (three tiers), WhatsApp, email, GitHub, X, Bluesky, Microsoft Teams and Signal, each with a drawn icon. Settings → Startup Dialog sets the presets, their order, how many are shown, the size offset and an encoder preset override.
- Squisher button and Autosave toggle in the Settings button's hover flyout, beside Queue and Video Combinator.
- Ctrl + wheel zooms the layer and audio time bars; Shift + wheel or a middle-button drag moves along them. **Sync Master Timeline Stretch** in the Views menu makes the master timeline follow.
- Mute button on each sound in Editor Mode, and **Show/hide muted tracks**.
- Autosave (Settings → Behavior): writes `Name.autosave.hproj` in the background when the project has changed.
- Portable mode: `portable.txt` beside `HandPeg.exe`, or `--portable`, keeps tools in `deps\`, working files in `temp\` and everything else under `data\`.
- Scrollbar Thickness slider (Settings → Interface); the Settings window can be resized and every tab scrolls.

### Changed
- Editor Mode header is Project, mode switch and Settings. Queue and Video Combinator moved into the Settings flyout; Views moved to the playback bar, and the timeline checkboxes fold into it when the bar is narrow.
- Layer rows are laid out like audio rows, and the Layer Height slider now reaches the same heights as the audio one. X and Y are set with drag-to-change value boxes; their sliders are gone.
- In Editor Mode, audio rows show Mute, Duck, Voice, speed, gain and filters. The mode, codec and bitrate boxes remain in Encoder Mode. Mute is the same as Ignore / Drop: the sound is left out of the preview and the export.
- The startup dialog no longer blocks the main window; a click on the main window puts it away.
- Clicking anywhere on the master timeline moves the playhead there and scrubs while held.
- Subtitles tab: Edit Captions replaces the Layout Pane button; Import External Subtitles and Edit Subtitles sit at the bottom (in Editor Mode, under Legacy Subtitle Settings).
- The Source box and its Browse / Load button are side by side, so text is never hidden under the button.
- Scrollbars are flat and thin, without arrow buttons.
- Waveforms are 20 peak pairs per second drawn at the size shown, not bitmaps. Undo history is kept compressed.
- Save As is Ctrl+Alt+S. Ctrl+Shift+S is taken system-wide by AMD's graphics software on many computers and never reaches HandPeg.

### Fixed
- Subtitle Editor preview breaking when its timeline was scrubbed.
- Play in the Subtitle Editor doing nothing on the first click when the playhead was at the end.
- The side panes lying over the player when the window was narrowed.
- The Views button being squeezed out of a narrow playback bar.
- A blank gap between the Source box's text and its button.

### Compatibility
- 2.2.0 projects open unchanged. Projects with text layers or a subtitle track open in 2.2.0 without them.
- Encoding and style presets are unchanged.
- On first launch the Squisher presets are added, and the startup dialog shows the first four.

### Known limitations
- Image-based subtitles are shown in the player and can be retimed, but are not written into the export.
- There is no published Whisper build that uses AMD or Intel graphics, so on those computers transcription runs on the processor. A Vulkan build of `whisper.exe` set by hand in Settings → Tools is used as it is.
- Whisper reports progress in steps of about 30 seconds of sound, so short clips go straight to done.
- A straight double quote in a text layer is drawn as a typographic quote; one font, size and color per text layer.
- The Squisher always makes H.264 / AAC, and lowers the picture height when the bitrate is very low. Its preset sizes are the services' limits as known when written and may need correcting.
- Portable mode skips update checks, and has not been audited for what yt-dlp and .NET themselves write outside the folder.
- Subtitle edits are not on the undo stack.
- No speed control on the main video; only the first audio stream of a video layer is used; opacity can't be keyframed.

### Upgrading
Installed copies update automatically.

## HandPeg v2.2.0

### Highlights
- **Editor Mode gets its height back.** The top bar is gone in Editor Mode, so the player and timeline take the full height of the window. Project, Queue, Video Combinator, the mode switch, Panes and Settings sit in one row at the top of the side column, and the source box has a pane of its own.
- **One set of encoder controls for every encoder.** A single Encoder Preset box and a single Rate Control box serve software, NVENC, AMF and QuickSync, and the hardware options sit inline in the main panel instead of in separate vendor panels.
- **Layer and clip audio in Live Preview.** Sound from video layers and added audio clips is mixed into the player while it plays, instead of only being heard in the export.
- **Subtitle preview no longer slows the player.** Styled auto-captions are now drawn by the player itself rather than rasterized on the CPU for every frame, which fixes the scrubbing lag and stutter on long videos.
- **NVENC on older NVIDIA cards.** HandPeg now detects a driver that is too old for the newest FFmpeg and switches to the FFmpeg 8.1 build, which keeps NVENC working on the GTX 10 series.

### New
- Source pane (Editor Mode): a full-width path or URL box with an inline button that reads **Browse** while the box is empty and **Load** once it holds something. Enter does the same. Download Resolution sits on the pane's title row. Show or hide it from the Panes menu.
- AMF: Usage (transcoding, low latency and so on) and 10-bit output.
- QuickSync: ICQ rate control, lookahead with depth, and 10-bit output.
- 10-bit output for software encoders (`yuv420p10le`).
- Rate Control lists only the modes the selected encoder has: CRF, CQ, CQP or ICQ, plus CBR and VBR.
- NVENC presets are labelled, e.g. `p1 (Fastest)` to `p7 (Highest Quality)`; AMF presets read Speed, Balanced and Quality.
- Each hardware encoder is probed on its own, and so are the features that depend on the card's generation (temporal AQ, 10-bit, B-frames). Options the card refuses are disabled instead of failing the encode.
- Settings → Update URLs → **Install FFmpeg 8.1 instead of the newest build (for older NVIDIA drivers)**. Turned on automatically when NVENC fails to start because of the driver version.
- Chapters are on the Video tab in Editor Mode, under Resolution & Cropping.
- The full Filters tab, including colour grading, is available in Encoder Mode.

### Changed
- Editor Mode has no top bar. Encoder Mode keeps its top bar exactly as before.
- The side column stays open in Editor Mode and cannot be dragged narrower than its row of header buttons, so the buttons never wrap or clip.
- The Panes menu opens from the header in Editor Mode.
- Automation rules moved from a tab in the main window to Settings → Automation.
- The NVENC, AMF and QuickSync panels are gone; Multipass, Usage, Lookahead, Spatial/Temporal AQ and 10-bit appear in one row and only for the encoders that have them.
- Preview Subtitles no longer switches Live Preview on; it works with Live Preview off.
- Paste & Load is removed in Editor Mode. Paste into the source box and press Enter.

### Fixed
- Scrubbing latency and playback stutter with Preview Subtitles on, worst on long videos.
- One hardware encoder failing its test (for example AV1 on a card without it) hiding the others.
- NVENC unavailable on GTX 10-series and older cards with the newest FFmpeg.
- Hardware encoder failures left no trace: FFmpeg's error output is now written to `HandPeg.log`, as is the reason for each fallback to software.
- 10-bit H.264 on QuickSync silently encoding as 8-bit. It is now reported as unsupported.
- Software fallback keeping hardware-only options (`-usage`, `-look_ahead`, `-look_ahead_depth`) that stopped the retry.

### Compatibility
- 2.1.0 projects and presets open unchanged. NVENC lookahead and 10-bit settings in older encoding presets carry over to the unified options.
- Encoding presets saved in 2.2.0 still load in 2.1.0, but lose AMF Usage, QuickSync ICQ, and lookahead and 10-bit on encoders other than NVENC.
- On first launch the Source pane is opened in every saved layout, and colour grading is switched on in Encoder Mode.

### Known limitations
- Live Preview audio plays layer and clip sound only while playing forwards. It is silent while scrubbing, in reverse shuttle and when a track is soloed, and it leaves out cuts, fades, track filters, ducking and voiceover. The export is unaffected.
- The Source pane can be shown and hidden but not dragged or reordered like the other panes.
- No speed control on the main video.
- Only the first audio stream of a video layer is used.
- Opacity can't be keyframed yet.
- Undo covers the timeline only.

### Upgrading
Installed copies update automatically.
**GTX 10-series and older NVIDIA cards:** after updating, let HandPeg run its FFmpeg update once (Settings → Tools). It installs the FFmpeg 8.1 build and NVENC returns on the next encoder check.
