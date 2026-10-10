# Changelog

Release notes for HandPeg, newest first. Notes for 2.1.0 and earlier are on the
[GitHub Releases page](https://github.com/gitclanged/HandPeg/releases).

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
