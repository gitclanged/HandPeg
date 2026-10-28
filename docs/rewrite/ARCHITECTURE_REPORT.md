# GPU-first NLE: Architecture Report

Written 2026-10-11 from HandPeg's working tree (`C:\Claude\HandPeg\HandPegApp`, branch `main` at `d8cb4ea`
"Stage 56 / v2.6.3", plus the uncommitted Stage 57/58 work: 30 modified and 7 untracked files). Nothing in the
repository was changed except creating this file.

**How this was produced**

- Read: every model, the full `FilterGraphBuilder`, `MpvPlayer`, `ProxyCache`, `SequenceCanvas`,
  `CaptionGenerator`, `TrackAudioFilters`, `VideoSurface`, the undo code, the live-preview scheduling in
  `MainWindow.xaml.cs`, and the method and command lists of all 15 `MainViewModel` partials (11,270 lines).
  Skimmed: the remaining services, dialogs and XAML. Also read `CHANGELOG.md`, all 28 commit messages, the
  project `CLAUDE.md`, and the older `HANDOVER_REPORT.md`, `RELEASE_REPORT.md` and `CHANGELOG_SOURCE.md` in the
  parent folder. Those three describe v2.0/v2.1 and are out of date in places. Where they disagree with the
  code, the code wins.
- HandPeg was **not run** for this report. No performance number below was measured by me. The numbers that
  come from HandPeg are the ones its own changelog and reports state.
- External facts (wgpu, FFmpeg, drivers, licences, whisper.cpp, egui) were checked by web search on
  2026-10-11. Sources are in §10. **[UNVERIFIED]** marks things I could not confirm and that a spike has to
  settle. **[ESTIMATE]** marks my own numbers.
- Size of what is being replaced: about 41,000 lines of `.cs` + `.xaml`. `MainWindow.xaml.cs` is 4,587
  lines. `MainViewModel` is a single class spread over 15 files and 11,270 lines.

---

## 0. The short version (blunt)

1. **The plan is sound, but the hard part is not the UI toolkit.** It is GPU media interop: hardware decode
   → your compositor → hardware encode, with no CPU copy, on 2 operating systems × 3 GPU vendors. Every other
   part of the editor is known engineering. Run Spike 1 (§5) before writing anything you intend to keep.
2. **Rust + wgpu + egui: I agree, with three changes.**
   - **wgpu is the renderer, not the interop layer.** Importing decoded frames, video queues, external
     semaphores and multi-planar (NV12/P010) imports all sit below wgpu's public API, in `wgpu-hal`. That API
     is explicitly unstable: `create_texture_from_hal` gained a required `initial_state` argument in wgpu 30
     (July 2026). Without it, wgpu treated imported frames as uninitialized, which under the Vulkan spec
     discards their contents. Vulkan multi-planar DMA-BUF import is still missing (wgpu#9801). So: keep all
     of this in one crate, pin the wgpu version, upgrade on purpose, and expect to own 1–2k lines of `ash`.
   - **"Vulkan-centered" has a hole on Windows: Intel.** The most complete public table of Vulkan Video
     driver support (Igalia, updated 2026-05-22) lists Windows drivers for NVIDIA and AMD, but lists no
     Windows support from Intel at all. You need either a D3D11VA→Vulkan shared-handle fallback, or wgpu's DX12
     backend on Windows. Decide by measuring in Spike 1, not by preference. WGSL shaders run unchanged on
     either backend.
   - **egui for the panels, yes. The viewer and timeline must be your own wgpu passes** (you already plan
     this). egui's wgpu backend has one open report of 1–7 s startups versus 100–300 ms on glow, with no cause
     found, and one extra frame of input latency on Windows at default settings (egui#7761). The suggested fix
     is `desired_maximum_frame_latency: 1`. Both problems directly threaten your launch and latency targets,
     so measure them in Phase 0.
3. **HandPeg's biggest lesson:** compiling effects into FFmpeg filter-graph strings makes next-frame preview
   impossible by construction. Nearly every performance feature HandPeg grew exists to work around that:
   proxies, the timeline proxy, quarter-size preview blur, half-rate preview, the 90 ms debounce, EDL stand-ins,
   the 30-minute black canvas file, and rebuilding the audio graph after every seek. Port none of these except
   per-file proxies, which are still useful for long-GOP footage.
4. **Most of your targets are realistic. Two are not, as written, and all of them need a measurable
   definition before they can be gates** (§0.1). "500 fps uncapped" is a vanity number. "≤ 1 frame scrub
   latency" is achievable for intra-frame media and not quite for short-GOP media.
5. **Licensing has to be decided before Phase 5, ideally now.** An LGPL FFmpeg linked dynamically is fine for
   a closed app, but it rules out x264/x265. Shipping those makes the app GPL. HandPeg's repository has no
   licence file at all. See §9, question 1.

### 0.1 Your targets: realistic or not, and the measurable version

| Your target | Verdict | Measurable version (this becomes the phase gate) |
|---|---|---|
| Cold launch to interactive window < 1 s | **Realistic, at risk.** A native Rust binary starts in tens of ms. The risks are GPU initialization (Vulkan instance and device, shader pipeline compiles on first run or after a driver update), the egui-wgpu startup anomaly, and antivirus scanning a new executable. HandPeg already reaches about 500 ms to its window (CHANGELOG 2.6.1). | Process start → first frame that **accepts input** with the shell drawn: **p95 ≤ 1.0 s** over 20 runs with a warm disk cache; **≤ 1.8 s** on the first run after install or a driver change (empty pipeline cache). Window visible ≤ 400 ms. Measured by in-app timestamps written to the log, as HandPeg's `Startup: window drawn N ms` line already does. |
| UI frame ≤ 2 ms, "500 fps uncapped" | **2 ms of CPU is realistic. 500 fps is meaningless.** Presentation is decided by vsync and the system compositor (DWM, Wayland), and video content only changes at the source rate. What users feel is CPU frame cost and input-to-photon latency. | **UI CPU time** (input handling → command buffers submitted): **p50 ≤ 1.0 ms, p99 ≤ 2.0 ms** on the reference desktop with the reference project (200 clips, 8 tracks, inspector open); p99 ≤ 3.0 ms on the reference laptop/iGPU. GPU time for the UI pass ≤ 0.5 ms. **Input-to-photon ≤ 2 refresh intervals** at 60 Hz with vsync (FIFO, max frame latency 1). Uncapped present is a benchmark switch, not a goal. |
| Effect parameter change visible on the next frame | **Realistic.** It is a uniform-buffer update; nothing is rebuilt. | Paused, dragging a slider: the presented frame reflects the value **within 1 refresh at p95 and 2 at p99**. Playing: in the next presented frame. Gate in `bench`: synthetic slider drag over 10 s. |
| Scrubbing ≤ 1 frame latency on intra / short-GOP media | **Intra-frame: realistic. Short-GOP: not quite.** Reaching frame *k* after an I-frame means decoding *k* frames. Even at a few ms per 1080p frame **[ESTIMATE]**, a 15-frame GOP costs 1–3 refreshes. | Time from scrub event to the **exact** frame presented: intra ≤ 1 refresh (p95); GOP ≤ 15 at 1080p ≤ 3 refreshes (p95); long-GOP: the nearest decoded frame within 1 refresh and the exact frame ≤ 150 ms (p95). During a scrub the UI frame-time gate still holds. |
| Zero dropped frames, typical 4-layer 1080p60 project | **Realistic on a discrete GPU** for H.264/HEVC 8-bit 4:2:0 with hardware decode. **Not** for HEVC 4:2:2 10-bit camera footage on GPUs whose decoder can't handle 4:2:2, ProRes/DNxHR on the CPU, or phone VFR footage without conform. | The benchmark project `bench-4x1080p60` (defined in Phase 0) plays 10 minutes with **0 late frames** (a frame presented after its scheduled vsync, read from the presentation log) and **0 audio underruns** on each reference machine. A separate "heavy" project tracks how drops degrade but is not a gate. |
| Preview and export share one render path | **Realistic. It is the core of the design.** | Same shader and graph code, and preview differs only by documented knobs (resolution scale, proxy choice). **Golden test:** an exported frame, before encoding, compared with a preview frame rendered at export resolution: PSNR ≥ 50 dB (that is, the same render up to floating-point noise). |
| FFmpeg only for demux/decode/encode, zero-copy hardware decode | **"No CPU copy" is realistic. "No copy at all" is not always.** FFmpeg's hardware frame pools must be returned quickly, or the decoder stalls. | **No CPU access to pixel data for hardware-decoded frames, and at most one GPU-side copy per frame.** The decoder reports bytes copied by the CPU, and the gate is 0 on hardware paths. |
| Windows and Linux, Vulkan-centered | **Realistic, if you accept at least three decode-interop paths** (§3.6). | Spike 1's matrix passes for each operating system × vendor cell you own hardware for. |
| Every HandPeg editing feature carried over | **Realistic across phases.** Several features should be redesigned rather than ported (§1). | Parity checklist (§1) fully ticked at the end of Phase 8. |
| (HandPeg's rule) idle memory < 200 MB | **Keep it.** HandPeg's debug build measured 223–271 MB idle (Handover §13). | Idle working set ≤ 200 MB with no project open. VRAM is budgeted separately and configurable. |

---

## 1. Feature inventory

**Legend.** Mapping: **[S]** GPU shader or pass · **[A]** audio DSP or engine · **[M]** project data model ·
**[X]** external library or tool · **[U]** UI · **[C]** cache or background job · **[P]** platform service.
Disposition: **Carry** (same behaviour) · **Redesign** (same need, different mechanism) · **→Encoder** (stays in
HandPeg, which becomes the encoding utility) · **→Squisher** (companion app) · **Drop**.

### 1.1 Sequence, timeline, editing

| Feature | What it does in HandPeg | Where | New architecture | Disp. |
|---|---|---|---|---|
| Sequence | Editor Mode edits a sequence with no main video. Its frame (width, height, fps) comes from the first video placed; saved as `Sequence.Frame` in a format 3 `.hproj` | `MainViewModel.Editor.cs` (`StartSequenceAsync`, `SyncSequence`), `ProjectStore.cs`, `Services/SequenceCanvas.cs` | [M] `Sequence { resolution, fps: Rational, sample_rate, color: ColorSpec }`. No canvas file: the editor owns its clock | Redesign |
| Tracks and clips | Tracks are implicit: clips sharing `TrackId` (and the same file). A split makes two clips. A track's position, look and filters are copied across its clips by reflection | `Models/Layer.cs:208-219` (`IsClipProperty`), `MainViewModel.Creator.cs:962-992` (`OnLayerChanged` → `GetTrackProperty`) | [M] First-class `Track`/`Clip` with stable IDs. Clips of any file can share a track. Properties belong to the clip; "apply to all clips on track" is an explicit command (§9, Q7) | Redesign |
| Move, trim, split, delete, duplicate; trim start/end to playhead; split at playhead (S) | Context menu and time bars | `MainViewModel.Timeline.cs` (`DeleteClip`, `TrimClip`, `DuplicateLayer`), `Creator.cs` (`SplitLayer`, `SplitSegment`), `MainWindow.xaml.cs:3390-3560` | [M] commands; [U] timeline tools. Add ripple, roll, slip and slide (§9, Q8) | Carry + extend |
| Recycle bin | Deleted clips listed with a preview and restorable | `Models/Sequence.cs` (`BinItem`, `DeletedClip`), `Sequence.cs` (`MoveToBin`, `RestoreFromBin`, `EmptyBin`) | Unlimited command undo plus autosave versions cover it. Keep only if you actually use the panel | Redesign/Drop |
| Clip speed 0.1–10× | Video-file and audio clips; the clip's length scales; keyframes are rescaled (rounded to 1 ms each time); pitch is kept | `Layer.SetSpeed` (`Layer.cs:257-280`), `FilterGraphBuilder.BuildRetime`/`BuildTempo`, `MainWindow` speed submenu | [M] `speed: Rational`, keyframes rescaled exactly; [S] frame mapping by PTS (frame blending later); [A] Signalsmith Stretch (MIT) | Carry |
| A/V link, unlink, slip, relink snaps | A video layer's sound follows it; unlinked, it can be slipped by `AudioOffset` | `Timeline.cs` (`SetAudioLinked`, `SlipAudio`), `Layer.AudioOffset` | [M] `LinkGroup` of clips; slipping is an offset of the linked audio clip | Redesign |
| Multiple audio streams of one file | Each stream after the first becomes a linked `Audio` clip (absolute stream index, `amovie=si=N`) | `Editor.cs` `AddAudioStreams`, `Layer.AudioStream` | [M] one clip per stream in the link group; [X] libav stream selection | Carry |
| Linked views (HUD piece, backdrop) | A crop of a video (`ShowsPart` plus Source fractions) or its blurred fill, mirrored to the video's clips on every command build | `Editor.cs:200` `SyncLinkedTracks`, `AdoptSourcePieces`, `AddBackgroundBlur` | [M] a derived clip referencing its source clip ID (follows by construction, never re-synced); [S] crop and blur | Redesign |
| Cut segments, play only kept segments, skipped segments, manual cut entry | Encoder Mode's kept stretches (I/O keys) | `Models/CutSegment.cs`, `MainViewModel.cs` (`AddStartPoint`/`AddStopPoint`), `ManualCutWindow` | Editor: In/Out range for export plus delete/ripple. Cut lists stay in HandPeg | →Encoder |
| I-frame marks, snap to I-frames, magnetism | Shows codec I-frames; snaps cuts for stream copy | `Creator.cs` (`SeekToIFrame`, `UpdateIFrameMarks`), `AppSettings` `SnapTimelineToIFrames`, `TimelineMagnetism` | [U] snapping to edits, playhead and markers. The I-frame index is kept internally for seeking [C]; I-frame snap is a stream-copy idea | Redesign/→Encoder |
| Undo/redo | 20 Brotli-compressed JSON snapshots of the timeline only | `Timeline.cs:38` (`UndoDepth`), `:59` `CaptureTimeline`, `:98` `RestoreTimeline` | [M] command system over immutable snapshots; covers **all** document state (§3.5) | Redesign |
| J/K/L shuttle | 2/4/8×; reverse simulated by a seek every 100 ms | `MainWindow.xaml.cs:416-426`, `:1677` `_shuttleTimer`, `:1717` `ShuttleBack` | [P] real reverse playback from a GOP cache (§3.6); audio reversed or muted | Redesign |
| Transport keys | Space, ←/→ frame step, I/O, S split, Delete, Ctrl+Z/Y, Ctrl+S, Ctrl+Alt+S (never Ctrl+Shift+S: AMD's overlay takes it) | `MainWindow_PreviewKeyDown` (`MainWindow.xaml.cs:352-436`) | [U] remappable shortcut table (keep the AMD note) | Carry |
| Master timeline | 40 thumbnail tiles taken only from the timeline proxy (blank after any edit), waveform, press-to-scrub, right-drag range | `MainViewModel.Thumbnails.cs`, `TimelineFilmstrip`, `TimelineSlider_*` | [U] one GPU timeline plus an overview strip. Thumbnails come from the per-media cache [C] and never go blank on edit | Redesign |
| Time bar zoom and pan | Shared zoom and offset (Ctrl/Shift+wheel, middle-drag), 1-2-5 time rulers, scrollbars | `TimelineTrack.cs` (`TimeRuler`), `MainWindow._timelineZoom` | [U] GPU timeline | Carry |
| Clip filmstrips and waveforms | Drawn in `OnRender`, filmstrips from FFmpeg, peaks at 20 min/max pairs per second | `TimelineTrack.cs`, `Timeline.cs` `LoadLayerFilmstripAsync`, `AudioTools.Waveforms` | [C] tile and peak-pyramid caches; [S] instanced quads and a waveform shader | Carry |
| Clip hover previews | A small FFmpeg decode per 5 s step, one in flight | `MainWindow.ClipHover.cs` | [U] read from the thumbnail cache, else a low-priority decode from the decoder pool | Carry |
| Track heights, show/hide muted, hide dropped tracks | Sliders and toggles | `Audio.cs` (`SetHeight`, `HideDroppedTracks`) | [U] | Carry |
| Mute and solo | Mute is the same as Ignore/Drop; solo plays one track | `AudioTrack.IsMuted`, `ToggleSolo` | [M] track mute/solo; [A] | Carry |
| Remove Dead Air | `silencedetect` (threshold dB, minimum length); modes Classic, Split & Mark, Delete; on a clip or track | `Creator.cs:1494`, `FfmpegRunner.DetectSilenceAsync`, `DeadAirDialog` | [A] native RMS/gate detector on decoded audio, Silero VAD optional for speech-aware cuts; [C] job; results applied as undoable split, mark or delete commands | Carry |
| Chapters | Chapter markers, chapters at cuts (metadata file) | `Creator.cs` `WriteChaptersFile`, `ProjectState.ChapterMarkers` | [M] markers with a chapter flag; [X] libavformat chapters | Carry |
| Timecode display | HH:MM:SS:FF or .mmm; no drop-frame | `Models/Timecode.cs`, `TimeDisplay.cs` | [M] timecode with drop-frame | Carry + fix |
| Playback rate | Speed box for the player | `PlaybackRate` | [P] | Carry |

### 1.2 Layers and compositing

| Feature | What it does | Where | New architecture | Disp. |
|---|---|---|---|---|
| Layer kinds | `Video` (region of the main video), `Image`, `Text`, `VideoFile`, `Audio`, `Captions`, `MainVideo`, `Background`, all in one class | `Layer.cs:6-31` | [M] clip kinds `Media{video/image/audio}`, `Text`, `Solid`, `Adjustment` (new), plus a caption **track**. `MainVideo` and `Background` disappear | Redesign |
| Placement | Fractions of the frame: top-left corner, width, free height or locked aspect, centre-anchored resize, −200..200 % out of frame | `Layer.cs` (`GetOutputRect`, `OnSizeWidthChanged`) | [M] transform { position (anchor-relative), scale, rotation, anchor, crop } in **normalized frame units**; [S] vertex transform | Carry (units kept) |
| Rotation, flips | `rotate`, `hflip`, `vflip`; animated rotation forces a square box of the diagonal's size | `FilterGraphBuilder.AppendLayer` | [S] part of the transform | Carry |
| Opacity | Static only (`lutyuv=a=val*k`) | `AppendLayer` | [S] animatable | Carry + fix |
| Rounded corners, feather | `geq` mask built once per layer | `FilterGraphBuilder.BuildLayerMask` (`:2303`) | [S] signed-distance rounded rectangle with feather | Carry |
| Drop shadow | Split, black, `boxblur`, offset overlay | `BuildFrameEngine` shadow branch | [S] alpha-blur shadow (radius, angle, distance, opacity; animatable) | Carry |
| Chroma key | `chromakey=color:similarity:blend` | `AppendLayer` | [S] YCbCr-distance keyer with spill suppression and matte choke/feather | Carry + improve |
| Masks | B/W image mask stretched over the layer; HUD shapes (panel, ellipse, slant, fade) generated as PNGs | `AppendLayer` (cmask), `HudLibrary.EnsureMask`, `LayerToolDialog` | [S] image masks plus parametric shape masks (SDF), invert, feather, animatable; [M] a mask list per clip | Redesign |
| Per-layer filters | contrast, brightness, saturation, gamma (`eq`), hue, sharpen (`cas`), blur (`gblur`), denoise (`hqdn3d`), LUT; alpha set aside around them | `FilterGraphBuilder.BuildLayerFilters` | [S] effects (§3.8) | Carry |
| Stacking order | Up/down; captions always on top, background at the bottom | `Creator.cs` `MoveLayer*` | [M] track order | Carry |
| Background blur, backdrop | Scale to fill, blur, dim; OpenCL in export, quarter size in preview | `BuildFrameEngine`, `Editor.cs` `AddBackgroundBlur`, `BlurSettingsDialog` | [S] "Fill/Backdrop" effect (dual-Kawase blur), identical in preview and export | Carry |
| Centre zoom and offset of the main video | `GetCenterRect` | `Creator.cs` | [M] the ordinary transform (no "main" clip) | Redesign |
| Output size presets, vertical video | Size boxes, Vertical Video toggle | `Creator.cs` `SetOutputSize`, Video tab | [M] sequence settings and templates | Carry |
| Crop, Draw Target | Rectangles dragged on the source video. Live Preview is **switched off** while drawing | `Creator.cs` (`DrawTarget`, `ResetCrop`), `MainWindow.xaml.cs:3786-3900` (`OverlayMode`) | [U] crop and region tools drawn on the composited viewer | Redesign |
| Layout pane | A separate canvas for arranging layers with resize handles | `MainWindow.RedrawLayout`, `ResizeAdorner.cs` | [U] transform gizmo directly on the video (single viewer) | Redesign |
| HUD layers per game | Overwatch, Counter-Strike 2, Apex Legends, Marvel Rivals; fractional rectangles plus shapes; vertical layout | `Services/HudLibrary.cs`, `HudMasksDialog` | [M] template data (JSON) producing clips with regions and shape masks. Port the data as-is | Carry |
| Style presets (`.hpstyle`) | Layout, colour, blur, subtitles sections; masks embedded as Base64; built-ins per game; import choosing sections; apply on drop | `Models/StylePreset.cs`, `StyleLibrary.cs`, `Color.cs` (`ApplyStyle`, `ExportStyle`), `StyleImportDialog` | [M] "Templates": a serialized subtree (tracks, effects, caption style). [X] importer for `.hpstyle` (§9, Q6) | Redesign |
| Legacy watermark → image layer | Preset migration | `Creator.cs` `AddLegacyWatermark` | importer only | Drop |
| Stream-copy safeguard | Warns that layers or filters are bypassed by Copy | Summary tab | not applicable | →Encoder |

### 1.3 Motion

| Feature | What it does | Where | New architecture | Disp. |
|---|---|---|---|---|
| Keyframes | X, Y, Scale, Rotation. A UI keyframe pins all four at once. Times are clip-local seconds; tolerance 0.02 s | `Layer.cs:511-800`, `Sequence.cs` (`RecordKey`, `AddKeyframe`, `GoToKeyframe`, `ApplyKeyframes`), Keyframes pane | [M] per-parameter curves for **any** animatable parameter (opacity, effect parameters, masks). Times in flicks; tolerance = half a sequence frame. "Key all transform properties" stays as a UI convenience | Redesign |
| Easing | Linear, Ease In-Out (default), Ease In, Ease Out, Smoothstep; per segment, set on the leaving key | `Models/EasingType.cs`, `Layer.Ease`, `FilterGraphBuilder.FormatEasing` | [M] the same presets plus Hold and Bézier handles (later); evaluated on the CPU in f64 and passed as uniforms | Carry + extend |
| Constant-key folding | Avoids per-frame `rotate`/`scale` for keys that never change | `FilterGraphBuilder.FoldConstantKeys` (`:363`) | unnecessary: a transform is a matrix | Drop |
| Keyframe diamonds on blocks, Keyframes pane | | `TimelineTrack.cs` (`KeyTimelineBar`) | [U] curve lane per clip | Carry |

### 1.4 Colour and whole-picture filters

| Feature | What it does | Where | New architecture | Disp. |
|---|---|---|---|---|
| Global grade | contrast, brightness, saturation, gamma, hue, RGB balance (as `eq` per-channel gammas, chosen to avoid an RGB conversion), sharpen (`cas`) | `MainViewModel.Color.cs` `BuildColorFilters`, `ColorControls.xaml` | [S] grade node, on an adjustment track or the master | Carry |
| LUT `.cube` | Global and per layer | `lut3d` | [S] 3D texture, tetrahedral interpolation, applied in a declared input encoding | Carry |
| Denoise | `hqdn3d` | `BuildVideoFilters` | [S] spatial (bilateral or NL-means-lite) first; temporal later (needs frame history, which interacts with scrubbing) | Carry (v1 spatial) |
| Deinterlace | `yadif` | `BuildVideoFilters` | [S] bwdif-style shader in the source's input transform | Carry |
| HDR → SDR | `zscale`+`tonemap=hable` | `FilterGraphBuilder.TonemapFilters` (`:400`) | [S] in the input transform (PQ/HLG → linear → gamut map → BT.2390 or Hable) | Carry |
| Fades | 1 s fade in/out of the whole output | `BuildWholeVideoFilters` | [S]/[A] clip fade handles and transitions | Redesign |
| Frame Preview dialog | One graded frame | `FramePreviewDialog` | the viewer is live | Drop |
| Pixel aspect, frame-rate conversion, colourspace | Output options | Video tab, `GetSampleAspectRatio`, `GetTargetFramerate` | export settings; [S] resample in the output transform | Carry |

### 1.5 Text, captions, subtitles

| Feature | What it does | Where | New architecture | Disp. |
|---|---|---|---|---|
| Text layers | Font, size (pixels of the output frame), colour, outline, multiple centred lines. Box measured by WPF, drawn by `drawtext` (two engines, "room to spare") | `Services/TextLayout.cs`, `FilterGraphBuilder.AppendTextSource` (`:2219`), Text Settings panel | [S] one text engine: shaping with cosmic-text, GPU glyph atlas (§3.10); styled runs and alignment (new) | Redesign |
| Auto-captions (whisper.cpp) | Extract 16 kHz mono WAV, run `whisper.exe -ojf`, rebuild words from tokens; model, language, prompt, translate; Silero VAD with times mapped back by **scraping whisper's log**; beam, best-of, temperature, fallback; VAD parameters; progress parsed from `-pp` | `Services/CaptionGenerator.cs` (`VadSegmentRegex` `:79`, `HelpAsync` `:300`), `Captions.cs`, `Subtitles.cs` | [X] whisper.cpp **linked as a library**, built in CI with Vulkan + CPU (+ optional CUDA): progress callbacks and word timestamps through the API, VAD in-process; [C] job; result = caption cues in the model (undoable) | Redesign |
| Caption style | Font, size, primary/highlight/outline colours, outline thickness, maximum words per line; animations Karaoke Sweep and TikTok Pop; caption box (position, size, opacity, shadow, mask) | `Models/CaptionStyle.cs`, `CaptionStyleDialog`, `CaptionLayer`, `CaptionGenerator.WriteAss` | [S] caption renderer drawing per-word animation from data (no ASS, no libass); [M] style on the caption track | Redesign |
| Captions as timeline data | Generate Captions transcribes once; asks before replacing existing cues | Stage 51/52, `GenerateCaptionsCommand` | [M] | Carry |
| Captions upon render | Deferred whisper at export; looping placeholder text; locked track | Stage 57 (`CaptionsOnRender`, `IsCaptionPlaceholderActive`) | [M] + [C]; placeholder drawn by the same caption renderer | Carry |
| Caption audio source | Voice-marked sounds mixed (`BuildSoundMix`), a chosen track, or a layer's sound | `FilterGraphBuilder.cs:315`, "Use Its Sound for Auto-Captions" | [A] offline render of the selected buses → whisper | Carry |
| Subtitle import | Hand-written SRT/VTT parser (spans); PGS `.sup` and VobSub read for timing only; image tracks are preview-only and retimed by rewriting the file | `Services/SubtitleFiles.cs` | [X] port the text parsers; decode bitmap subtitles with libavcodec into image cues that **also render in export** (fixes a known limitation) | Carry + fix |
| Subtitle Editor | Separate window with its own mpv on a tiny black file; zoomable timeline under a fixed playhead; drag to retime; word dividers; split; add segment/word; delete; global offset (ms) | `Views/SubtitleEditorWindow*`, `Views/SubtitleTimeline.cs` | [U] edit captions **in the main timeline** with a word sub-lane, plus the inspector. No second player (§9, Q12) | Redesign |
| Source subtitle streams | Pass through as soft subs, or burn in | `SubtitleTrack` actions | [X] mux soft subtitles; burn = render the cue track. Add sidecar SRT/VTT export (new) | Carry (export side) |

### 1.6 Audio

| Feature | What it does | Where | New architecture | Disp. |
|---|---|---|---|---|
| Gain −40..+24 dB per track/clip | `volume=` | `TrackAudioFilters.BuildChain` | [A] smoothed gain, animatable (new) | Carry |
| Filter chain per sound | high-pass, noise gate, de-noise (`afftdn`), compressor, bass/treble, 10-band EQ, pitch/tempo, stereo widener, limiter, in that order | `Models/TrackAudioFilters.cs`, `TrackAudioFiltersDialog` | [A] native DSP: biquads, envelope followers, RNNoise-style or spectral de-noise, look-ahead limiter; same order and parameters | Carry |
| Auto-duck | Per sound Duck/Voice; voices summed as the key; `sidechaincompress` by N dB; **export only** | `FilterGraphBuilder.BuildAudio`, `Sequence.cs` `ToggleDucking` | [A] side-chain bus, **in preview and export** | Carry + fix |
| Normalize | `loudnorm` I=−16 TP=−1.5 LRA=11 | `NormalizeFilters` | [A] two-pass: EBU R128 measurement, then gain and a true-peak limiter; a preview approximation | Carry |
| Audio pieces | Split and mute parts of a track (`volume=0:enable=`) | `AudioTrack.Pieces`, `SplitAt`, `ToggleMuted` | [M] ordinary audio clips | Redesign |
| Voiceover studio | DirectShow device list, record/pause/resume/discard (FFmpeg subprocess, `q` typed to stdin), import file, trim, placed at the playhead as a Voice clip; `Voiceovers` folder | `AudioTools.cs:231` `VoiceRecorder`, `Audio.cs`, `VoiceoverStudioDialog` | [P] cpal input stream → WAV writer thread; record/punch-in from the track header; kept in the project folder | Redesign |
| Live preview audio | Layer sound mixed by `amovie` in mpv, forwards only; no cuts, fades, filters, ducking or voiceover | `BuildLiveAudioGraph` (`:958`), `ApplyLiveFilter` | [A] the real engine, identical to export | Redesign |
| Output tracks | Per output track codec/bitrate, passthrough, mix selected tracks, legacy voiceover | `AudioTrack`, `BuildAudio` | export: channel layout and track mapping (stems later) | Mostly →Encoder |
| Master Mix View | Encoder Mode | | | →Encoder |
| Seek from waveform click | | `MainWindow` | [U] | Carry |

### 1.7 Media, proxies, caches

| Feature | What it does | Where | New architecture | Disp. |
|---|---|---|---|---|
| Probe | `ffprobe` → `MediaInfo` | `Services/MediaProbe.cs` | [X] libavformat in-process; cached per media fingerprint | Redesign |
| Proxies | 720p (Medium) or 360p (Low), GOP 15, capped at 4 or 2 Mbit/s, NVENC > QSV > AMF > libx264, silent; LRU size limit; 7-day sweep; cancelled on project switch; named by path + size + mtime | `Services/ProxyCache.cs`, `Project.cs` (`RefreshProxies`, `MakeProxiesAsync`) | [C] keep the concept; in-process encode; invalidated **only** by a change of media fingerprint; used automatically for long-GOP or heavy codecs | Carry |
| Preview Quality tiers | High = originals, Medium/Low = proxy tiers | `AppSettings.PreviewQuality` | [U]/[S] separate the knobs: preview **resolution scale** and **proxy usage** | Redesign |
| Timeline proxy | Whole-timeline render; auto-renders after 60 s idle; dropped by **any** edit | `Project.cs` (`RenderTimelineProxyAsync`, `TimelineEdited` `:692`) | [C] segment render cache keyed by a per-segment hash (§3.15), late phase, often unnecessary | Redesign |
| GPU adapter selection | DXGI adapter list for decode; player hardware acceleration toggle | `Services/GpuAdapters.cs` | [P] one adapter choice for the whole app (render, decode, encode) | Carry |
| "Loading..." clips | A placeholder block while probing | `Layer.IsLoading` | [M] media state `Pending`; placeholder drawn | Carry |
| Import from URL (yt-dlp) | Resolution, subtitles, SponsorBlock categories, download cache trimming | `Services/YtDlpDownloader.cs` | [X] yt-dlp binary, downloaded and updated at runtime (Unlicense) | Carry |
| Video Combinator | Up to 5 videos; hard splice, crossfade, dip to black; stream-copy concat when compatible; send to editor | `Services/VideoCombinator.cs`, `CombinatorDialog` | "New sequence from clips" plus transitions on the timeline. Stream-copy concat goes to HandPeg | Redesign/→Encoder |
| Drag-and-drop import | Onto a track at the drop time; overlay of drop targets (style presets); deferred handlers | `MainWindow.xaml.cs` (`Window_PreviewDrop`, `BuildDropTargets`) | [U] drop zones drawn in egui (no airspace); [P] see the winit Wayland note in §2.4 | Carry |
| Replace video | Dialog to replace the open source | `ReplaceVideoDialog` | [M] relink or replace media command | Redesign |

### 1.8 Export

| Feature | What it does | Where | New architecture | Disp. |
|---|---|---|---|---|
| Encoders | libx264/x265/SVT-AV1/VP9/ProRes/DNxHR; NVENC/QSV/AMF H.264/HEVC/AV1; preset, tune, profile, level; rate control CRF/CQ/CQP/ICQ/CBR/VBR; multipass, AQ, lookahead, 10-bit | `MainViewModel.Command.cs` (`BuildVideoCodecArguments`, `BuildNvencArguments`, `BuildQsvArguments`), `Models/EncodingPreset.cs` | [X] libavcodec encoders through the API; `ExportPreset` model; GPU frames handed over directly (§3.13) | Carry |
| Containers | mp4/mkv/mov/webm/gif/webp; faststart; metadata titles | `BuildFfmpegCommand` | [X] libavformat; GIF/WebP via our own quantizer (no libavfilter) | Carry |
| Target file size | Bitrate from size | `TargetFileSize` | export option | Carry |
| Encoder probing | `-encoders`, then a 3-frame test per encoder and feature; logged | `Services/EncoderProber.cs` | [P] background probe on first run or driver change; results cached | Carry |
| Hardware → software fallback | Rewrites the command line and retries once | `Services/HardwareFallback.cs` | [X] fallback chain inside the exporter | Redesign |
| FFmpeg 8.1 for older NVIDIA drivers | Swaps FFmpeg build | `DependencyUpdater`, setting `UseFfmpegForOlderNvidiaDrivers` | One FFmpeg ships with the app. FFmpeg 9.0 dropped NVIDIA SDK < 11.1, so decide minimum hardware (§9, Q3) | Redesign |
| Batch queue | Add, edit, update, pause, remove; captions transcribed just before encode; per-job snapshot | `MainViewModel.Queue.cs`, `QueueWindow`, `QueueJob` | [U] render queue; jobs hold an immutable project snapshot | Carry |
| Render Preview | Short encode of segments | `RenderPreviewAsync`, `PreviewPlayerWindow` | real-time preview | Drop |
| Size estimate, completion notification, taskbar progress | | `Media.cs` `EstimateOutputSize`, `AudioTools` notifications, `SetTaskbarProgress` | [P] | Carry |
| Command preview / manual command | Editable FFmpeg command | Command Preview tab, `ManualCommand` | no CLI in the editor | →Encoder |

### 1.9 Project and persistence

| Feature | What it does | Where | New architecture | Disp. |
|---|---|---|---|---|
| `.hproj` | Format 3 (sequence; tracks of clips; defaults omitted; keyframes as flat arrays); format 2 read and converted | `Services/ProjectStore.cs` | [M] new versioned format; [X] one-way importer for `.hproj` v2/v3 (§9, Q6) | Redesign |
| Save, Save As, autosave | Ctrl+S, Ctrl+Alt+S; `Name.autosave.hproj` every N minutes off-thread; save-on-exit dialog; project manager; recent projects | `Project.cs` (`AutosaveAsync`, `SaveProjectAs`), `SaveOnExitDialog`, `ProjectManagerWindow` | [M] immutable snapshot serialized off-thread; versioned autosaves; crash recovery on start | Carry |
| Mode profiles | Separate UI layouts for Encoder and Editor Mode | `AppSettings` `ModeProfile` (reflection) | one editor; named workspace layouts | Drop |
| Settings | 150+ properties in one class | `Services/AppSettings.cs` | [P] typed, versioned settings file; settings search | Redesign |
| Backup export/import | Settings, presets, projects as a zip | `BackupExporter`/`BackupImporter` | [P] later phase | Carry |
| Legacy data migration | Pre-Velopack locations | `DataMigration.cs` | optional "import HandPeg settings" | Drop |

### 1.10 Application infrastructure

| Feature | What it does | Where | New architecture | Disp. |
|---|---|---|---|---|
| Updater | Velopack for installed copies; GitHub `releases/latest` check for portable copies and build folders | `Services/AppUpdater.cs` | [P] Velopack's Rust crate: Windows installer, Linux AppImage | Carry |
| Portable mode | `portable.txt` or `--portable`: `deps\`, `temp\`, `data\` beside the exe; skips registry and update | `Services/AppPaths.cs` | [P] one paths module; every path goes through it | Carry |
| Dependency manager | Downloads FFmpeg/libmpv/yt-dlp/whisper/models; SHA-256 check; version stamps; atomic swap; user overrides; prompts for exactly what is missing | `DependencyUpdater.cs`, `DependencyPanel`, `DependencyPromptWindow`, `RequireAsync` | FFmpeg and whisper.cpp **ship with the app** (built in CI). Runtime downloads only: whisper/VAD models and yt-dlp, keeping SHA-256 checks and atomic swap | Redesign |
| First-run wizard | Theme, mode, encoder, dependency tier | `FirstRunWindow` | [U] minimal; model download asked for when first needed | Redesign |
| Theme | Dark/light, Windows accent colour | `ThemeManager.cs` | [U] egui visuals; accent from the platform | Carry |
| Log and action history | `HandPeg.log`; status bar flash; the last 12 actions on hover | `AppLog.cs`, `Log()` | [P] `tracing` with a rolling file; the action history comes free from command labels | Carry |
| Lazy startup | Tabs as templates, player after first paint, probes deferred | Stage 53/54 | [P] startup sequencing (§3.16) | Carry (as a principle) |
| Session temp folder | Created per run, deleted on exit | `SessionPaths.cs` | [P] | Carry |
| Shortcut and settings tabs, scrollbar thickness, startup window size | | `SettingsWindow` | [U] | Carry |
| Elevation and drag-drop | Elevated process refuses Explorer drops (UIPI) | user memory note | [P] detect elevation and warn | Carry |

### 1.11 Out of scope

- **Encoder Mode** (becomes HandPeg): batch converter UI, Automation smart rules (folder/keyword/extension →
  preset), presets bar, stream-copy remux and demuxer cuts, I-frame snapping, Command Preview and manual
  command, cut-segment workflow, Master Mix View, auto-captions at export, Recent Editor Files.
- **Social Sharing Squisher** (`Services/SocialSquisher.cs`, `SplashWindow`, `SquishView.cs`): companion app (§8).

---

## 2. Lessons learned

### 2.1 What limits performance

1. **Effects are FFmpeg filter strings, so every edit rebuilds the world.** Any property change calls
   `GenerateCommand` (`MainViewModel.cs:1806`), which rebuilds the whole export command and raises
   `LiveFilterInvalidated`. That restarts a **90 ms debounce** (`MainWindow.xaml.cs:1221`). `ApplyLiveFilter`
   (`:1499`) then builds a fresh graph string and hands it to mpv as `lavfi-complex` (`MpvPlayer.SetFilterGraph`,
   `MpvPlayer.cs:440`). mpv tears down and re-creates the filter graph, re-opens every `movie=` source, and an
   exact seek follows. So the floor on preview latency is the debounce plus a graph rebuild, by construction.
   It also reruns `TimelineEdited()` (`:1831`), which discards the timeline proxy. **Rule for the new app:
   parameters are uniforms; the graph is rebuilt only when topology changes, and even then nothing is
   re-opened.**
2. **Layer video is decoded on the CPU and composited on the CPU.** Layers are read with `movie=` inside the
   graph (`FilterGraphBuilder.BuildFileSource`, `:1999`), which never uses the hardware decoder. The main
   input switches mpv to `hwdec=auto-copy` while a graph runs (`MpvPlayer.cs:352`), so GPU-decoded frames are
   copied back to RAM. Overlays, masks and blurs then run on the CPU in `yuva420p`. The same applies to the
   export: `-hwaccel` only covers `-i` inputs, and layers are `movie=` sources. Proxies, `LiveScaleSteps`
   (`:1000`), `fps=rate/2` above 40 fps (`:1511`), the quarter-size blur and the timeline proxy all exist to
   make a CPU pipeline bearable.
3. **"Preview" and "export" are two different renderers that look alike.** The live graph:
   - takes its canvas from 2×2 pixels of the player's own frame, darkened and stretched, to keep mpv's clock
     (`:1721`);
   - blurs at 1/16 the area and scales with `fast_bilinear`;
   - halves the frame rate;
   - has captions drawn by libass in the player, while export burns them with `ass=`;
   - plays layer sound only forwards, without cuts, fades, track filters, ducking or voiceover
     (`BuildLiveAudioGraph` comment, `:958`).

   What you approve in preview is not what you export.
4. **The app never owned its clock.** mpv needs a seekable file, which produced:
   - `SequenceCanvas` (30 minutes of 64×36 black per frame rate, `Services/SequenceCanvas.cs`);
   - EDL stand-in stretches muted by a player audio filter;
   - `amovie` restarting at 0 after seeks, so the audio graph is laid out from a base moment and rebuilt after
     every jump (`aresample first_pts`);
   - reverse shuttle faked by a seek every 100 ms (`MainWindow.xaml.cs:1717`);
   - generated `lavfi` sources that can't be seeked (the Subtitle Editor bug).

   **Rule: the audio device clock (or a monotonic clock) drives presentation, and the editor schedules
   decode itself.**
5. **Startup was won by heroics.** About 500 ms to the window (CHANGELOG 2.6.1) needed lazy `DataTemplate`
   tabs, hand-declared fields filled by `FindName`, off-thread JSON, deferred probes and the player started
   after first paint (Stages 53–54). That is good work, but WPF plus the .NET runtime stayed above the 200 MB
   idle target. A native binary starts with these advantages built in.
6. **Every background task is a process.** Thumbnails, hover frames, proxies, waveforms, silence detection,
   probing, whisper and even the microphone recorder (FFmpeg `dshow`, `AudioTools.cs:231`) spawn processes,
   with priority set after start and progress scraped from stderr. Process start-up costs, parse failures and
   lost structure (word timings come from a JSON file; VAD segments come from **regex on whisper's log**,
   `CaptionGenerator.cs:79`) follow from that.
7. **Per-frame expression evaluation for motion.** Keyframes become `eval=frame` expressions. An animated
   rotation forces a square box of the diagonal's size. An animated scale re-scales per frame. Stills have to
   be turned into streams to animate, and `FoldConstantKeys` exists to avoid a per-frame `rotate` for keys that
   never change.

### 2.2 What limits correctness

8. **Time as `double` seconds, rounded at every boundary.**
   - `StartTime`, `Duration` and `MediaOffset` are double seconds; the playhead is double milliseconds.
   - `KeyTolerance = 0.02` s (`Layer.cs:517`) is "half a frame" only at 25 fps.
   - `SetSpeed` rounds every keyframe time to 3 decimals on **each** change (`Layer.cs:272`), so repeated
     speed changes drift.
   - Every number written into a filter is `0.###` (`FilterGraphBuilder.Number`, `:408`), a 1 ms grid that can
     move a cut onto the neighbouring frame.
   - 29.97 is rescued by a lookup table (`BroadcastRates`, `:1582`).
   - `Timecode` has no drop-frame (`Models/Timecode.cs`).

   **Rule: integer time (§3.4) and exact rational rescaling.**
9. **Undo is opt-in and covers only part of the document.** Before each change the code must remember to call
   `Checkpoint()`. There are 48 call sites; a forgotten one is a silently non-undoable edit. Snapshots cover the
   timeline only: subtitles (CHANGELOG "Subtitle edits are not on the undo stack"), grading, caption style and
   export settings are excluded. Undo is 20 deep and rebuilds every `Layer` (`Timeline.cs:38,59,98`).
   **Rule: every document change is a command, and the type system makes that the only way to mutate.**
10. **The playhead writes into the document.** `ApplyKeyframes` (`Sequence.cs:804`) evaluates keyframes and
    *sets* `PositionX/Y`, `SizeWidth` and `Rotation` on the model as playback moves. Base values and animated
    values are the same fields, guarded by an `_applyingKeys` flag to stop a command rebuild. **Rule:
    evaluation is a pure function; it never writes to the model.**
11. **Implicit structure kept consistent by side effects.** Tracks are integers shared by clips (and
    `ProjectStore` requires the same file). Track-wide properties are copied by reflection, against a
    hand-kept list of clip-only names (`Layer.IsClipProperty`, `Layer.cs:215`). Linked tracks are re-mirrored
    on every command build (`SyncLinkedTracks`, `Editor.cs:200`). A sequence pretends to be a "source" through
    a fabricated `MediaInfo` (`SetMediaInfo`, `Media.cs:61`). Each of these is a place where a new property or
    feature breaks something silently.
12. **Command-line limits and escaping.** The export is one command string run through CliWrap. I found no
    guard for Windows' 32,767-character `CreateProcess` limit and no use of `-filter_complex_script` (grep).
    A project with many keyframed layers can exceed it. Escaping is fragile: `EscapeDrawText` turns a
    straight `"` into a typographic quote because it cannot survive the command line (CLAUDE.md "Text
    Layers").
13. **No colour management; compositing in 8-bit gamma-encoded 4:2:0.** Everything is `yuv420p`/`yuva420p`.
    Alpha is chroma-subsampled, so key and mask edges fringe. Blends happen on gamma-encoded values. Grading is
    `eq` on YUV. 10-bit sources are truncated by compositing. Tone mapping is an export-only chain. There is no
    handling of matrix, range or primaries beyond FFmpeg's defaults.
14. **Three text engines.** WPF measures text layers, `drawtext` draws them (`TextLayout.cs:13`: "room to
    spare at the sides for the little that two text engines differ by"), and libass draws captions from
    generated ASS.

### 2.3 What limits maintainability

15. **God objects.** `MainWindow.xaml.cs` (4,587 lines) holds player lifecycle, live-filter scheduling, drag
    and drop, pane layout, timeline drawing, overlays and context menus. `MainViewModel` is one class of
    11,270 lines with hidden-mode flags: `_applyingKeys`, `_copyingToTrack`, `_restoringProject`,
    `_isBackgroundWorker`, `_writingGeneratedCommand`, `_commandDeferrals`.
16. **One `Layer` class for everything.** About 70 fields, plus fake audio-row properties (`Title`, `Action`,
    `Codecs`, `Bitrate`, `IsSolo`…, `Layer.cs:311-424`) so one XAML template binds to `Layer` and `AudioTrack`.
    Each saved field lives in four places (`Layer`, `LayerState`, `ToState`, `FromState`) plus the `ProjectStore`
    mapping.
17. **UI types inside the model.** `Layer.Waveform` and `Layer.Filmstrip` are WPF `ImageSource`
    (`Layer.cs:298,301`), and so is `BinItem.Preview`. Models are `ObservableObject`s, so the model can't be
    used headless or tested without WPF.
18. **Moving-target dependencies.** FFmpeg came from BtbN's "latest", whisper from upstream releases (CPU and
    CUDA only for Windows), and libmpv from a nightly build. Behaviour detection runs `whisper --help` and greps
    the output. An NVIDIA driver problem was answered by switching to a different FFmpeg build.
19. **Two products in one document.** Encoder Mode and Editor Mode share a window and a document: the mode
    switch stashes the sequence, converts main video ↔ sequence (`ConvertMainToLayerAsync`), and Stage 58 had to
    remove "the main video" everywhere it leaked. You are already splitting them. Keep it that way: the new app
    has **no main-video concept at all**.

### 2.4 Platform pitfalls that carry over to any stack

20. **Airspace.** A native child window (`VideoSurface : HwndHost`) always covers WPF content, so overlays
    lived in a second transparent owned window (`VideoSurface.cs`). mpv swallowed file drops
    (`drag-and-drop=no`, Stage 48). The drop overlay had to hide the video. Live Preview is **switched off**
    while drawing a crop or target (`MainWindow.xaml.cs:1519`). The new design removes all of this with one
    swapchain. **Do not embed a foreign window again**, not even for a "temporary" player.
21. **Windows UIPI.** An elevated process silently refuses Explorer drops. Still true for the new app.
22. **winit gaps** (relevant to the Rust choice): outgoing drag (drag a file *out* of the app) is not part of
    winit; the `drag` crate covers Windows and macOS, and Linux only through GTK. File drops on native Wayland
    are reported broken in winit as of May 2026 **[UNVERIFIED in winit's tracker]**. Budget a small
    `nle-platform` crate for drag-out, the clipboard file list, notifications and taskbar progress.

### 2.5 What HandPeg got right (keep)

- **Resolution-independent units.** Layout stored as fractions of the frame, and blur radii given "for a
  1080-wide frame". Keep normalized units everywhere.
- **Content-addressed caches** (path + size + mtime), atomic writes (`making_*.part` → rename), LRU limits,
  a startup sweep that never deletes what is in use, and cancellation that kills child work at once and checks
  `ReferenceEquals` so a stopped run never clobbers the next (Stage 56).
- **Background work at lower priority with thread caps** (Stage 58.3).
- **SHA-256-verified tool downloads and atomic swaps.** User-chosen tool paths are never overwritten.
- **Templates as data** (HUD tables, `.hpstyle` with embedded masks), and value-type keyframes.
- **Startup measured on every launch and logged.**
- **Captions treated as timeline data**, never re-transcribed behind the user's back (Stage 51).

---

## 3. Proposed architecture

### 3.1 Shape

```
                    ┌────────────────────────────── UI thread (winit + egui) ───────────────────────────────┐
 input ───────────▶ │ panels (egui) · timeline pass (custom wgpu) · viewer pass + overlays (custom wgpu)     │──▶ one swapchain
                    │ dispatches Commands ──▶ Document (immutable snapshots, Arc) ──▶ publishes Snapshot      │
                    └───────────────▲───────────────────────────────┬───────────────────────────────────────────┘
                                    │ latest composite (texture)     │ Snapshot + time + quality
                    ┌───────────────┴──────────── render thread ─────▼───────────────────────────────────────────┐
                    │ RenderGraph::evaluate(snapshot, t) → passes (WGSL) → linear fp16 composite → output xform │
                    └───────────────▲───────────────────────────────────────────────────────────────────────────┘
                                    │ GPU frames (NV12/P010 textures in own pool)
     ┌──────────────────────────────┴─ media service ────────────────────────────┐   ┌──── audio ──────────────────┐
     │ demux threads → decoder pool (HW: Vulkan Video / D3D11VA / VA-API / CPU)  │   │ mixer worker (DSP, stretch,│
     │ read-ahead, prewarm of upcoming clips, keyframe index, GPU frame cache    │   │ duck) → lock-free ring →    │
     └───────────────────────────────────────────────────────────────────────────┘   │ device callback = CLOCK    │
     background pool (low priority): thumbnails · waveforms · proxies · whisper ·      └─────────────────────────────┘
     dead-air · loudness · render cache · autosave writer
     export job: same RenderGraph at export resolution → YUV pack (compute) → HW/SW encoder → libavformat mux
```

### 3.2 Workspace crates (names are placeholders)

| Crate | Owns | Must not depend on |
|---|---|---|
| `nle-time` | `Time` (flicks, i64), `Rational`, timecode incl. drop-frame, rescaling | anything |
| `nle-model` | Project/Sequence/Track/Clip/Effect/Keyframe/Caption data, `Command`s, undo/redo, invariants, serde format + migrations, `.hproj`/`.hpstyle` importers | GPU, FFmpeg, UI |
| `nle-media` | FFmpeg FFI (`ffmpeg-sys-next` raw + own safe wrapper), probe, demux, decoder pool, encoders, mux | UI |
| `nle-gpu` | device creation (incl. `ash` device shared with wgpu), **all** interop (Vulkan Video frames, D3D11 shared handles, DMA-BUF), texture pools; the only `unsafe` besides FFI | model, UI |
| `nle-render` | render graph, effect registry (WGSL + manifests), colour transforms, text/caption renderer, geometry shared with overlays | UI |
| `nle-audio` | engine, DSP, time-stretch, ducking, loudness, device I/O (cpal), offline render, recording | GPU, UI |
| `nle-cache` | thumbnails, waveforms, keyframe index, probe cache, proxies, render cache; disk index and eviction | UI |
| `nle-ai` | whisper.cpp + VAD bindings, transcription jobs | GPU (it creates its own ggml backend) |
| `nle-platform` | drag-out, clipboard file lists, notifications, taskbar progress, elevation check, paths/portable mode | — |
| `nle-ui` | egui shell, docking, panels, timeline widget, viewer widget, inspector | FFmpeg directly |
| `nle-app` | `main`, startup sequencing, settings, updater, logging, crash handling | — |
| `bench` | headless and scripted benchmarks, gates | — |
| `xtask` | `finish-phase`, FFmpeg/whisper build helpers, licence checks | — |

`#![forbid(unsafe_code)]` everywhere except `nle-gpu` (interop) and `nle-media`/`nle-ai` (FFI).

### 3.3 Threads and scheduling

| Thread | Job | Rules |
|---|---|---|
| UI (main) | winit events, egui layout, timeline and viewer recording, overlays, present | No file I/O, no FFmpeg, no lock held by a worker for more than ~50 µs. Reads snapshots via `ArcSwap`. Event-driven redraw when idle, continuous while playing or animating |
| Render | evaluates the render graph for (snapshot, time, quality) into a ring of 3 output textures; renders immediately on a parameter change while paused | One `wgpu::Queue` shared with the UI. Submission ordering is documented; output handoff by fence/submission index |
| Demux (per open source) | `av_read_frame` with read-ahead; packet queues per stream | Bounded queues; generation counter for seeks |
| Decoder workers (pool) | one per active stream plus prewarm slots; hardware contexts | Copy or convert out of FFmpeg's surface pool immediately (§3.6); drop stale generations |
| Audio device callback | real-time: copy from a lock-free ring, master gain, update clock | No allocation, no locks, no syscalls |
| Audio mixer worker | decode audio, DSP, stretch, duck, mix 100–200 ms ahead | Priority above normal |
| Background pool | thumbnails, waveforms, proxies, whisper, dead-air, loudness, render cache | OS priority below normal; cancellation tokens; GPU work submitted in small batches with a per-frame budget so it never pushes the UI past its gate |
| File watcher | missing/changed media → relink prompts | — |
| Autosave writer | serializes an immutable snapshot | Atomic temp + rename; versioned |

**Clock.** The audio device's playback position (samples consumed, corrected for the latency it reports)
is the master. With no audio device, a monotonic clock. Video presents the frame whose PTS covers the
clock at the next vsync and logs late frames, which feeds the "0 late frames" gate. Frame pacing for a
sequence rate that differs from the display rate is explicit: each frame's present count is decided ahead,
and VRR is used where available.

### 3.4 Time model

- **`Time(i64)` in flicks** (1/705,600,000 s). 705,600,000 = 2⁹·3²·5⁵·7², which divides evenly for 24, 25, 30,
  48, 50, 60, 90, 100, 120 and 240 fps, for their NTSC ×1000/1001 variants (one frame at 30000/1001 is exactly
  23,543,520 flicks), and for every common sample rate (8 k to 192 k, including 44.1 k). Range is about 414
  years.
- **Rates are `Rational`** (`30000/1001`). Media timestamps are converted from the stream's `time_base` with
  exact i128 math and a single documented rounding rule.
- **Positions in the UI snap to sequence frame starts.** The render time of frame *n* is
  `n × frame_duration` exactly.
- **Speed is `Rational`** for constant speed, so changing speed rescales keyframes exactly with no drift.
  Speed ramps come later, as curves sampled at frame times.
- **VFR sources** are mapped by PTS: show the frame with the largest PTS ≤ the media time. HandPeg needed
  special cases for this; here it is the default.
- **Keyframe times are clip-local `Time`.** Tolerance is half a sequence frame, computed, not a constant.
- Serialization writes times as integers; the file carries the time unit name to future-proof it.

### 3.5 Project model, commands and undo

```rust
// sketch, not final
struct Project { id, media: PVec<Media>, sequences: PVec<Sequence>, templates, settings: ProjectSettings }
struct Sequence { id, format: SeqFormat /* res, fps, sample rate, colour */, tracks: PVec<Track>, markers, captions: PVec<CaptionTrack> }
struct Track { id, kind: Video|Audio, clips: PVec<Clip> /* sorted, non-overlapping unless allowed */, mute, solo, lock, visible, name }
struct Clip  { id, link: Option<LinkGroupId>, start: Time, duration: Time, source: Source /* Media{id,stream,in} | Text | Solid | Adjustment | Derived{of: ClipId, region} */,
               speed: Rational, transform: Transform, effects: PVec<EffectInstance>, masks: PVec<Mask>, opacity: Param<f32>, audio: AudioProps }
struct Param<T> { base: T, keys: Option<Curve<T>> }       // evaluation never writes back
struct EffectInstance { effect: EffectId /* "nle.color.eq@1" */, enabled, params: Map<ParamId, Param<Value>> }
struct CaptionTrack { style: CaptionStyle, cues: PVec<Cue { start, end, words: Vec<Word{text, start, end}> }>, placeholder: bool }
```

- **Immutable snapshots with structural sharing** (`im`/`rpds` persistent vectors, or `Arc` + copy-on-write).
  A snapshot is `Arc<Project>`: free to hand to the render thread, the autosave writer or an export job.
- **The only way to mutate is `Document::apply(Command) -> Result<Applied>`.** Fields are private to
  `nle-model`. A `Command` is data (enum) with a label for the action history.
- **Undo is a stack of previous `Arc<Project>` roots.** Cheap thanks to sharing. The limit is memory (for
  example 256 MB or 500 steps), not 20.
- **Interactive drags are transactions:** begin on press, update the working snapshot live (the preview
  follows), commit one undo step on release, roll back on Escape.
- **Everything in the document is undoable:** clips, effects, keyframes, captions and subtitles, project
  export settings, templates applied. View state (zoom, scroll, panel sizes, playhead) is not.
- **Invariants are checked after each command in debug and test builds:** no illegal overlaps, links
  consistent, times non-negative, IDs unique.
- **Property tests (proptest)** drive random command sequences and check that apply/undo/redo round-trips
  restore the identical snapshot, and that save → load is identical.

### 3.6 Media: demux, decoder pool, read-ahead, hardware interop

**Decoder pool**

- A decoder is leased per (media, stream, playback context). Two clips showing the same frames share them
  through the **frame cache** (key = media, stream, PTS), not through graph tricks.
- **Read-ahead:** while playing, each active decoder keeps *K* frames ahead (for example 8 at 60 fps) in VRAM.
- **Prewarm:** clips starting within the next ~2 s are opened, seeked to their in-point and decoded to their
  first frame before the cut. Cut boundaries are the classic cause of dropped frames.
- **Keyframe index** per media, built by a background packet scan at import and cached. Seeks go to the
  nearest I-frame ≤ target and decode forward.
- **Progressive scrubbing:** present the nearest available frame (cache, then I-frame) at once, then the
  exact frame. Only the newest scrub target counts; stale work is dropped by generation. This keeps HandPeg's
  "newest position wins" pacing (Stage 47) without its 33 ms timer.
- **Reverse playback:** decode a GOP forward into the cache, present it backwards, prefetch the previous GOP.
  Memory-bounded; above 2× reverse, show I-frames only.
- **Surface pool hazard:** FFmpeg's hardware frame pools are small, and holding `AVFrame`s for caching starves
  the decoder. Policy: on decode completion, **copy or convert the planes into our own texture pool on the GPU
  and release the `AVFrame` at once** (this is "at most one GPU copy"). Raise `extra_hw_frames` only for
  read-ahead depth.
- **Synchronization without wgpu touching external semaphores:** because frames are decoded ahead, the render
  thread **polls the frame's timeline semaphore value without waiting** (`vkGetSemaphoreCounterValue`) and only
  uses completed frames. That sidesteps wgpu's lack of a public "wait on external semaphore", which is a known
  gap in its API. To be confirmed in Spike 1.

**Hardware decode paths** (driver minimums from the Igalia table; it may lag recent drivers):

| | NVIDIA | AMD | Intel |
|---|---|---|---|
| **Windows** | Vulkan Video via FFmpeg's Vulkan hwaccel on **our** `VkDevice` (H.264/HEVC ≥ 527.86, AV1 ≥ 538.35, VP9 ≥ 573.38) | Vulkan Video (Adrenalin H.264/HEVC ≥ 23.7.1, AV1 ≥ 24.8.1, VP9 ≥ 25.10.03.01) | **No Windows entry in the table.** D3D11VA → shared NT handle → `VK_KHR_external_memory_win32` import (planes as separate views), **or** wgpu DX12 backend + `d3d12va` |
| **Linux** | Proprietary driver Vulkan Video (H.264/HEVC ≥ 525.47.04, AV1 ≥ 535.43.24) | RADV (Mesa: H.264/HEVC ≥ 23.1.2, AV1 ≥ 24.0.3, VP9 ≥ 25.2). Fallback: VA-API → DRM PRIME → DMA-BUF import | ANV (Mesa: H.264/HEVC ≥ 23.1.2, AV1 ≥ 25.0, VP9 ≥ 25.2). Fallback: VA-API → DMA-BUF |
| **All** | CPU decode → plane upload through a staging ring (ProRes/DNxHR/odd formats; FFmpeg 8.1 added Vulkan-compute ProRes decode, worth evaluating) | | |

- **Device sharing.** We create the `VkInstance` and `VkDevice` with `ash`, with the video, external-memory and
  timeline-semaphore extensions and a decode-capable queue family. We wrap it for wgpu with
  `vulkan::Adapter::device_from_raw` + `Adapter::create_device_from_hal`, and give the same device to FFmpeg
  through `AVVulkanDeviceContext`. FFmpeg's Vulkan hwcontext requires Vulkan ≥ 1.3 in recent versions.
- **Multi-planar imports.** On Vulkan DMA-BUF, wgpu-hal's helper handles only single-plane images (wgpu#9801),
  so we hand-roll the import with `ash` and wrap it with `texture_from_raw` plus a drop callback (about
  120 lines of unsafe code, per that issue). For D3D12 NV12 a working pattern exists: `OpenSharedHandle`, wrap
  with `wgpu::hal::dx12`, expose Y as R8 and UV as RG8 (the filmcraft PR, reported byte-identical).
- **Which FFmpeg.** FFmpeg 9.0 (2026, a major version bump) modernized the Vulkan hwcontext API. FFmpeg 8.1
  (March 2026) is the conservative choice. Pick in Spike 1. Bindings: `ffmpeg-sys-next` 9.0.1 has feature flags
  up to FFmpeg 9.0; `ffmpeg-next` is in maintenance mode and covers FFmpeg up to 8.0; `rsmpeg` 0.18 targets 8.0.
  **Use the raw `-sys` crate plus our own thin wrapper.** No high-level crate covers `hwcontext_vulkan` anyway.

### 3.7 GPU frame cache

- **Source cache:** (media, stream, PTS) → texture in the native format (NV12/P010/RGBA8). An LRU under a VRAM
  budget, by default about 25 % of dedicated VRAM capped at 2 GB, and much smaller on iGPUs that share system
  memory **[ESTIMATE]**. A 1080p NV12 frame is about 3.1 MB, so 2 GB ≈ 650 frames.
- **Still and text cache:** images and rasterized text keyed by (content hash, size bucket).
- **Composite cache:** recent output frames at preview resolution keyed by (frame index, render hash), for
  scrubbing back and forth and for loops. The render hash covers only the clips that contribute to that frame,
  so an edit invalidates exactly the frames it touches.
- Every cache has pinned entries (in use by an in-flight submission). Eviction happens only after the
  submission completes.

### 3.8 Render graph and effects

- `evaluate(snapshot, t, quality)` → the visible clips bottom to top → for each clip:
  **input transform** (YUV → linear RGB: matrix, range, transfer, gamut; deinterlace; tonemap) → crop →
  **effect chain** → masks (SDF shapes and image masks, feather, invert) → opacity → **transform**
  (position/scale/rotation/flip around an anchor; bilinear or bicubic) → composite **over** an accumulator
  (premultiplied, linear `Rgba16Float`), with shadows as an extra pass → global adjustments, transitions,
  captions and text → **output transform** (display or encode).
- **Parameters are uniforms,** evaluated per frame on the CPU from curves (f64 → f32). Nothing about a
  parameter change rebuilds pipelines. Pipelines are created per effect × format at startup or lazily, and are
  kept in wgpu's pipeline cache on disk.
- **Effects have one of two shapes.** *Pixel-local* (colour, key, LUT, opacity) are WGSL functions
  `fn apply(c: vec4f, uv: vec2f, p: Params) -> vec4f`. Chains of them can be fused into one generated shader
  later (an optimization, not v1). *Neighbourhood* effects (blur, sharpen, denoise, shadow) get their own passes
  with ping-pong targets from a transient pool. No per-frame allocations.
- **Resolution independence:** every spatial parameter is in normalized frame units or "pixels at 1080
  reference" (as HandPeg did), so a ½-resolution preview matches export.
- **v1 effect set** (parity with HandPeg): input transform, crop, transform, opacity, eq
  (contrast/brightness/saturation/gamma), hue rotate, RGB balance, LUT, chroma key, blur (dual-Kawase and
  Gaussian), sharpen (CAS), spatial denoise, shadow, shape and image masks, backdrop fill, tonemap,
  deinterlace, fades and transitions (crossfade, dip to black), text and captions, output transform.

### 3.9 Colour pipeline

1. **Input.** Read matrix (601/709/2020), range (limited/full), primaries and transfer
   (BT.1886/sRGB/PQ/HLG) from the stream. Missing tags fall back to heuristics (SD → 601, HD → 709), with a
   per-media override in the inspector (HandPeg had none).
2. **Working space.** Linear light, Rec.709/sRGB primaries, `Rgba16Float`, premultiplied alpha. SDR-first.
   HDR sources are tone-mapped and gamut-mapped on input. An HDR project mode (Rec.2020 linear working space,
   PQ/HLG output) is a later phase.
3. **Each effect declares its space.** Grading controls behave like FFmpeg `eq` only in a display-encoded
   space, and `.cube` LUTs usually expect Rec.709 display-referred input. Those nodes encode → apply → decode
   internally. Compositing, blur, keying mattes and scaling stay linear.
4. **Output.** For display: encode to the swapchain format (sRGB/BT.1886 for SDR; scRGB or PQ for an HDR
   display later). For export: linear → BT.709 OETF → YCbCr (limited range by default) → **4:2:0 chroma
   downsampling with correct siting**, in compute, into NV12/P010 planes, with ordered dither when going to
   8-bit. Containers are tagged correctly (primaries, trc, matrix, range).
5. **Linear blending changes the look** of soft edges, text antialiasing and opacity crossfades compared with
   HandPeg's gamma-space overlays. Keep linear as the default. A per-project "legacy (display-space)
   blending" switch is cheap to add if imported HandPeg projects look wrong; decide after the importer exists.

### 3.10 Text and captions

- **One engine:** cosmic-text (shaping via rustybuzz/harfrust, font discovery and fallback, bidi, line
  breaking) → glyph rasterization (swash) into a GPU atlas at the target size. Outlines come from SDF bands of
  the same glyphs (thickness in output pixels), which also makes per-word scale animation crisp.
- **Text layers:** the box is measured by the same engine that draws, so there are no "two engines differ"
  margins. Any character, including straight quotes, renders, because nothing goes through a command line.
  Styled runs and alignment come later.
- **Captions:** cues hold words with absolute times. HandPeg's `WordBreaks` fractions are converted on import.
  Animations are data, evaluated per word in the shader: **Karaoke Sweep** (per-glyph progress) and **Pop**
  (current word scale 115 % plus highlight colour), plus room for more. Layout reproduces HandPeg's grouping
  rules: maximum words per line, a new caption after 0.7 s of silence or after `.`/`!`/`?`, a 0.2 s hold.
- **Bitmap subtitles** (PGS/VobSub) are decoded by libavcodec into image cues that render in preview **and**
  export.
- **Placeholder captions** are the same renderer looping `PlaceholderText`.

### 3.11 Audio engine and clock

- Internal format: f32 stereo at 48 kHz (project setting; mono/5.1 later).
- **Per clip:** decode (CPU) → resample (libswresample or `rubato`) → **time-stretch** (Signalsmith Stretch,
  MIT; best between 0.75× and 1.5×, so measure 0.1×–10× quality in Spike 4; Rubber Band is GPL or commercial)
  → fades and gain → filter chain (HandPeg's order) → track bus.
- **Ducking:** the voice bus feeds a key signal to the ducked tracks' side-chain gain (attack 15 ms,
  release 350 ms, depth in dB, as HandPeg's numbers). Active in preview.
- **Master:** loudness normalization. Export is two-pass (EBU R128 measurement with the `ebur128` crate, then
  gain and a true-peak limiter). Preview uses a running approximation.
- **The same engine renders preview** (real-time, via a lock-free ring to the cpal callback) **and export**
  (offline, faster than real time). Golden test: offline render == captured preview samples (before device
  resampling).
- **Scrub audio:** short windowed grains at the scrub position (optional). Reverse playback reverses chunks or
  mutes.
- **Recording:** cpal input → WAV writer thread; punch-in at the playhead; the clip is created by a command on
  stop (undoable); files go to the project's media folder.
- **Meters:** peak, RMS and short-term LUFS per track and master (new, cheap).

### 3.12 Viewer, overlays and timeline UI (one swapchain)

- winit window + wgpu surface + egui (`egui-wgpu`). **Viewer and timeline are custom render passes** inside
  egui-allocated rectangles (`egui_wgpu::CallbackTrait`): same encoder, same swapchain, no airspace.
- **Viewer:** shows the composite with the display transform. Overlays are drawn in the same pass in viewer
  space: transform gizmo with handles, crop, region (Draw Target), mask shapes, caption box, safe areas,
  guides. They use the **same geometry module as the compositor**, so what you drag is exactly what renders.
  Live preview stays on while you drag, unlike HandPeg.
- **Timeline:** instanced quads for clips, waveforms sampled from peak textures, thumbnails from a tile
  atlas, keyframe diamonds, text from egui's font atlas. Virtualized by visible time range. Hit-testing on the
  CPU with per-track interval lookups.
- **Panels:** egui with `egui_dock` (0.21 pairs with egui 0.36). Workspace layouts are saved by name.
  AccessKit for screen readers. Theme dark/light plus platform accent.
- **Frame pacing:** FIFO (vsync) with `desired_maximum_frame_latency = 1` (the egui#7761 fix); redraw on
  events only when idle.
- **Exceptions to "every pixel in one swapchain":** native file dialogs (`rfd`) and OS notifications. Those
  are fine.

### 3.13 Export path

- An export job holds an `Arc<Project>` snapshot, so editing during export is safe. It runs **the same render
  graph** at export resolution and full quality, offscreen, as fast as the encoder allows: frames N+1 and N+2
  render while N encodes.
- **Output transform → YUV planes in our textures → encoder input:**
  - **Vulkan Video encode** (FFmpeg `h264_vulkan`/`hevc_vulkan` since 7.1, `av1_vulkan` since 8.0) given our
    images as `AVVkFrame`: true zero-copy on NVIDIA (H.264/HEVC ≥ 538.09, AV1 ≥ 553.40) and AMD Windows (≥
    23.20.13.02; AV1 ≥ 24.20.25.01); Linux RADV (Mesa ≥ 24.1) and ANV (H.264/HEVC Mesa ≥ 24.3).
  - **NVENC** (richer rate control and AQ, which HandPeg exposed) via CUDA frames imported from Vulkan
    external memory, or D3D11 textures on Windows. **AMF** and **QSV** via D3D11 on Windows. Exact interop per
    vendor is settled in Spike 5.
  - **Windows D3D12:** FFmpeg has `hevc_d3d12va` encode (7.1), and 8.1 added H.264 and AV1 on D3D12. This is
    relevant if Windows ends up on the DX12 backend.
  - **Software:** async GPU readback through a 3-deep staging ring → libav encoders. LGPL-safe:
    ProRes/DNxHR (native), SVT-AV1 and libvpx (BSD), OpenH264 (BSD). GPL: x264/x265 (§9, Q1).
- **Audio:** the offline engine → libav encoders (native AAC, Opus (BSD), MP3 (LGPL), PCM, AC-3).
- **Mux:** libavformat with chapters, metadata, faststart, soft subtitle tracks and a sidecar SRT/VTT.
- **Fallback:** the hardware encoder fails → log the reason → next encoder in the chain (as
  `HardwareFallback.cs` did), never silently changing the codec family without saying so.
- **Render queue:** jobs are snapshots plus presets; captions-on-render transcribes inside the job before the
  encode (HandPeg 57.1 semantics, with whisper failures kept out of the encoder-fallback path).

### 3.14 Effect extensibility

- An effect is a **manifest** (TOML: id@version, name, category, parameters with type, range, default,
  animatable, units, working space) plus **WGSL**. Built-ins use the same format, compiled in with
  `include_str!`. A versioned id lets projects migrate parameters.
- Optional later: a user effects folder validated with naga at load. A hung shader can trip the GPU watchdog
  (TDR), so this stays off until there is a watchdog and safe-mode recovery.
- **No native plugins** (no DLL ABI, no OFX), consistent with HandPeg's "no rigid plugin architectures" and
  with keeping AI-written code memory-safe. Audio effects implement a Rust trait, built-in only. CLAP is a
  possible later step.

### 3.15 On-disk caches

- **Root** comes from the paths module (portable-aware). The index is SQLite (`rusqlite`, bundled; public
  domain) or `redb` (pure Rust): entries with key, size, last access and kind. Every write is temp + rename.
  The startup sweep never deletes what is in use (HandPeg Stage 56 rules).
- **Media fingerprint** = size + mtime + BLAKE3 of the first, middle and last 1 MB. Survives moves and renames,
  enables "relink by fingerprint", and catches changed files.
- **Thumbnails:** tiles at fixed time steps per zoom level (1/2/5/10/30 s…), 160 px tall, raw RGBA with LZ4,
  memory-mapped, uploaded straight into an atlas.
- **Waveforms:** a peak pyramid (min/max i16 at 256-sample buckets, then ×4 levels). About 5–6 MB per hour of
  stereo **[ESTIMATE]**.
- **Keyframe index and probe data:** small binary or JSON per fingerprint.
- **Proxies:** per (fingerprint, tier). H.264 GOP ≤ 8 via the hardware encoder, or intra ProRes Proxy for
  machines without one. Invalidated only by the fingerprint.
- **Render cache** (replaces the timeline proxy, later phase): the timeline is split at edit points; each
  segment's key = hash of everything contributing to that range + media fingerprints + quality tier. Editing
  one clip re-renders only the segments it overlaps. Used only when a segment can't play in real time.

### 3.16 Startup sequencing (sub-second)

1. `main`: args and portable detection; logging (async writer); settings read (tiny file) in parallel.
2. Create the window **hidden**. Bring up the wgpu instance, adapter and device with the on-disk pipeline
   cache. Load only the embedded UI font.
3. Build and present the first frame of the shell, then show the window (no white flash). Log
   `startup.first_interactive_ms`.
4. After first paint: **lazily load FFmpeg** (`dlopen` / delay-load), enumerate system fonts, read the
   hardware probe cache (keyed by driver version), and restore the last project (media opened lazily, decoders
   on first need).
5. Never at startup: whisper or its models, yt-dlp checks, the update check (background after about 30 s),
   cache sweeps (background, idle priority).

### 3.17 Robustness

- **GPU device loss** (Windows TDR, driver updates): detect it, recreate the device and pools, reopen
  decoders, re-render. The document is unaffected because it is immutable data.
- **Crash handling:** panic hook writes the log tail and the last command labels. Autosave versions are
  offered on next start.
- **Profiling:** `tracing` spans everywhere plus Tracy integration in development builds. The gates in §0.1
  read the same spans.

---

## 4. Stack comparison

Scores are 1 (poor) to 5 (best) for **this** project.

| | Rust + wgpu + egui | C++ + Qt 6 (Quick/RHI) | C# + Avalonia + Vulkan | Stay on WPF |
|---|---|---|---|---|
| **Performance ceiling** | 5: native, no GC; pays only for what you write | 5: native; Qt Quick's scene graph is an overhead you can bypass | 3: GC pauses are manageable but the render path crosses managed/native constantly; Avalonia owns the swapchain | 2: D3D9-era composition, airspace, no Vulkan |
| **Linux** | 5: winit (X11/Wayland), Vulkan native; gaps: drag-out, Wayland file drop **[UNVERIFIED]** | 5: Qt is the Linux desktop's own toolkit (KDE) | 4: works; GPU interop on Linux is less trodden | 0: Windows only. **Disqualified** |
| **Launch time** | 4: native fast; risk is GPU init and the egui-wgpu anomaly (egui#7761) | 4: 300–600 ms typical for Qt Quick **[ESTIMATE]**, QML compile caches help | 3: ReadyToRun or NativeAOT reach ~0.5–1 s **[ESTIMATE]** | 4: HandPeg reached ~500 ms with heavy effort |
| **HW decode interop difficulty** | 3: wgpu-hal unstable (v30 changed `create_texture_from_hal`), multi-planar Vulkan import missing, external semaphores not exposed; `ash` fills every gap; FFmpeg's C structs via raw `-sys` bindings | 4: everything is native C/C++; `QRhiTexture::createFrom` imports native textures (Qt 6.6+, but `QRhi` is a "GuiPrivate"-tier API); Qt Multimedia's FFmpeg backend has texture converters to learn from | 2: three unsafe layers (FFmpeg.AutoGen, Silk.NET Vulkan, Avalonia GPU interop); Avalonia's Windows interop path goes through ANGLE/D3D shared textures | 1: D3DImage on D3D9Ex shared surfaces |
| **AI-assisted development safety** | 5: what compiles is free of data races and use-after-free; `unsafe` is quarantined. Caveat: wgpu/egui API churn means models produce outdated code, which **fails to compile** (a safe failure) | 2: AI-written C++ looks plausible and hides undefined behaviour, lifetime bugs in FFmpeg refcounting, and Vulkan sync bugs. Sanitizers catch some | 4: memory-safe and models know C# well, but interop is unsafe and GC finalizers vs native handles is a classic bug source | 4: same as C#, irrelevant given Linux |
| **Ecosystem** | 3: wgpu, egui, cpal, cosmic-text, whisper bindings exist; FFmpeg bindings are thin (`ffmpeg-next` in maintenance mode, covering FFmpeg up to 8.0; raw `ffmpeg-sys-next` 9.0.1 current); no NLE components | 5: Qt (docking, text, accessibility), OpenTimelineIO, OCIO/OIIO, MLT (which you are deliberately not using) | 3: Avalonia is solid; FFmpeg and Vulkan bindings are maintained but thin; no NLE components | 3 |
| **Total (equal weights)** | **25** | **25** | **19** | **14 (disqualified)** |

**Verdict.** Rust and C++/Qt tie on points. **Pick Rust**, because the deciding factor for a project built
mostly with AI assistance is how wrong code fails: in Rust it mostly fails to compile, in C++ it ships
undefined behaviour. Qt's real advantage, easier interop, is concentrated in maybe 2,000 lines that Rust
handles with `ash` in one crate. Your existing C# skills favour Avalonia, but it scores worst exactly where
this project is hard: interop, and the swapchain is not yours. WPF is out (no Linux).

**What I would change in your plan:**

1. **wgpu, pinned, with an escape hatch.** All rendering is WGSL through wgpu. All interop is in
   `nle-gpu`. **Kill criterion (Spike 1):** if wgpu-hal can't import decoded frames on a cell without forking
   wgpu, the compositor moves onto raw `ash` (Vulkan) while the UI stays on wgpu sharing the device. The WGSL
   effects survive either way (naga can compile them to SPIR-V).
2. **Windows backend chosen by Spike 1:** Vulkan everywhere if the Intel path (D3D11VA → Vulkan import) works.
   Otherwise DX12 on Windows (`d3d12va` decode, D3D12 encode), keeping WGSL.
3. **egui only for panels.** If the egui CPU cost breaks the 2 ms p99 with a realistic inspector, replace the
   heavy panels (not egui wholesale) with retained custom widgets.

**Verified facts behind the scores** (sources in §10):

- **wgpu:** the latest is 30.0.1 (22 Aug 2026). v30 added `initial_state` to `create_texture_from_hal`
  (previously imports were treated as uninitialized, which discards contents under the Vulkan spec), and lets
  `copy_texture_to_texture` copy one plane of NV12/P010 into a single-plane texture. `ExternalTexture` and
  `Device::create_external_texture` exist (WebGPU `GPUExternalTexture`). I could not confirm per-backend
  support or a feature flag. Vulkan DMA-BUF import is single-plane only (#9801). wgpu-native has no C API for
  D3D12 shared handles (#621). Rust users reach it through `create_texture_from_hal`.
- **FFmpeg Vulkan:** decode H.264/HEVC/AV1 since 6.1; encode H.264/HEVC since 7.1; VP9 decode and AV1 encode
  since 8.0, plus compute FFV1 and ProRes RAW decode; ProRes encode/decode and DPX decode via compute in 8.1
  (March 2026), which also added H.264/AV1 D3D12 encode. 9.0 (2026) continued the Vulkan work (APV, new pixel
  formats, a modernized hwcontext API). 8.0 also gained a `whisper` audio filter, which we don't use because
  libavfilter isn't linked.
- **Drivers:** see the §3.6 table. Intel's Windows driver has no Vulkan Video entry.
- **Licensing:** FFmpeg's core libraries are LGPL 2.1+. `--enable-gpl` (x264, x265 and friends) makes the
  build GPL; `--enable-nonfree` makes it non-redistributable. Closed apps link an LGPL build dynamically, allow
  relinking, and ship the notices and source offer.
- **whisper.cpp:** official Windows release assets are CPU and CUDA. A request for Vulkan Windows binaries
  (#3673) was closed as not planned, so **we build whisper.cpp ourselves with `GGML_VULKAN=ON`**. That finally
  gives AMD and Intel users GPU transcription, a HandPeg known limitation.
- **egui:** typically 1–2 ms/frame of CPU and idle when nothing changes (docs). Large scroll areas must
  virtualize. Use `rayon` tessellation if needed. The wgpu backend reports above apply.
- **Velopack:** has a Rust crate (Windows installer, Linux AppImage, delta updates).

---

## 5. Risk spikes

Each spike is **throwaway code** in `spikes/NN-name/`, with a written result in `docs/history/` and numbers
in `bench/results/`. Spike 1 decides the architecture; the others decide parameters.

### Spike 1: hardware-decoded frame → WGSL shader with no CPU copy (Windows + Linux × NVIDIA/AMD/Intel) **[#1]**

- **Build:** a winit + wgpu window; FFmpeg via `ffmpeg-sys-next`; play (a) 1080p60 H.264 8-bit, (b) 4K60 HEVC
  10-bit (P010), (c) a 1080p60 OBS recording (VFR, long GOP). Paths:
  1. Vulkan Video through FFmpeg's Vulkan hwaccel on **our** `ash`-created device, shared with wgpu
     (`device_from_raw` → `create_device_from_hal`); the `AVVkFrame` image wrapped with
     `create_texture_from_hal(initial_state)` or copied plane-by-plane into our pool; sampled as Y (R8/R16) +
     UV (RG8/RG16) in WGSL; presented.
  2. Windows: D3D11VA → shared NT handle → Vulkan `VK_KHR_external_memory_win32` import; also measure the
     wgpu **DX12** variant (`d3d12va` or a D3D11 shared handle opened in D3D12).
  3. Linux: VA-API → DRM PRIME → multi-planar DMA-BUF import with `ash` (wgpu#9801 workaround).
  4. CPU decode → staging upload (the universal fallback, for comparison).
- **Measure per cell:** CPU bytes copied per frame (must be 0 on hardware paths), decode→sample latency,
  GPU time, sustained fps with **4 simultaneous streams**, VRAM, readiness polling via timeline semaphore.
- **Pass:** every OS×vendor cell you can test has at least one no-CPU-copy path sustaining 4×1080p60 with
  ≤ 2 frames decode→sample latency.
- **Kill or pivot:** wgpu-hal can't do it without a fork → compositor on raw `ash`, UI on wgpu. No Vulkan path
  on Intel Windows → DX12 backend on Windows. Vulkan Video unstable on a vendor → that vendor uses the
  D3D11VA/VA-API import path.

### Spike 2: compositor + UI frame budget (egui + custom passes) on dGPU and iGPU

- **Build:** using the decoders from Spike 1, 4 layers with transform, opacity, chroma key, blur and one text
  layer; an egui shell with docked panels and a 500-clip GPU timeline; launch timing instrumentation.
- **Measure:** UI CPU p50/p99, GPU composite time per layer count, input-to-photon (high-speed phone camera or
  latency markers), **cold launch to first interactive frame** on both operating systems, the egui-wgpu
  startup anomaly, memory.
- **Kill or pivot:** UI p99 > 2 ms after virtualization → custom retained widgets for the timeline and
  inspector. Launch > 1 s because of wgpu init → investigate the pipeline cache and backend choice; if still
  over, show the window on a minimal software-drawn frame first.

### Spike 3: seek, scrub and reverse latency; decoder pool

- **Build:** the decoder pool with keyframe index, read-ahead, prewarm and progressive scrub; a timeline of
  1-second clips alternating between 4 files at 60 fps; reverse at 1×/2×.
- **Measure:** exact-frame scrub latency for intra (ProRes/DNxHR/MJPEG), GOP-15 H.264, long-GOP HEVC and VFR
  OBS footage; late frames at cuts; behaviour when `AVFrame`s are held (pool exhaustion).
- **Kill or pivot:** long-GOP exact scrubbing unacceptable → proxies on by default for long-GOP media
  (intra-ish proxies, generated in the background).

### Spike 4: audio engine, clock and A/V sync

- **Build:** cpal output (WASAPI shared, PipeWire/ALSA); 8 tracks with the HandPeg filter chain, Signalsmith
  Stretch at 0.1×–10×, ducking; video presentation driven by the audio clock.
- **Measure:** underruns, output latency, CPU per track, **A/V drift over 30 minutes** with VFR video and
  44.1 kHz audio, stretch quality at extremes, offline render == preview capture.
- **Kill or pivot:** drift > 1 frame per hour or underruns at 8 tracks → revise the clock design (for
  example, rate-adjusting video against the audio clock), or reduce DSP in preview.

### Spike 5: export path, encoder interop and the licence build

- **Build:** render a 10-minute 1080p60 4-layer timeline through the render graph → YUV pack → (a)
  `h264/hevc/av1_vulkan` zero-copy, (b) NVENC via CUDA or D3D11 interop, (c) AMF, (d) QSV, (e) software. CI job
  builds FFmpeg (LGPL configuration, Vulkan on) and whisper.cpp (Vulkan + CPU) for Windows and Linux.
- **Measure:** fps against **HandPeg v2.6.3 exporting the same project on the same machine** (the baseline),
  quality (VMAF/PSNR vs a lossless reference), and golden equality of the preview frame vs the export frame
  before encoding.
- **Kill or pivot:** Vulkan encode rate control or quality clearly worse than NVENC/AMF/QSV → vendor encoders
  via interop become the default and Vulkan encode becomes the fallback.

---

## 6. Phased roadmap

Every phase ends with `scripts/finish-phase` (§7): build, tests, benchmark gates, history file, commit, tag,
push. **Gates are measured on named reference machines** (§9, Q2). CI has no GPUs and runs correctness
only: golden tests on lavapipe (Linux) and WARP (Windows, if the DX12 backend is chosen).

| Phase | Deliverable | Perf/quality gates | Explicitly deferred |
|---|---|---|---|
| **0: Spikes + benchmark harness** | Spikes 1–5 with written verdicts; backend decision record; `bench` crate with reference media **generated by FFmpeg** (`testsrc2` with burnt-in frame numbers so frame-exactness is machine-checkable; tones for audio); reference projects; HandPeg baselines measured; CI skeleton building empty Windows and Linux apps on tags | Spike pass criteria; harness repeatability ±5 % over 5 runs | everything product-facing |
| **1: Skeleton + model** | `nle-time`, `nle-model` (commands, undo, transactions, serde format, migrations scaffold), app shell (window, egui, docking, settings, logging, paths/portable, startup sequencing, crash hook), empty viewer/timeline passes | Cold launch p95 ≤ 1.0 s; UI CPU p99 ≤ 2 ms (empty and a 200-clip synthetic model); idle RAM ≤ 200 MB; 100k random-command property test: undo/redo/save/load round-trip identical | media, rendering |
| **2: Media + playback** | Probe, decoder pool with the Spike 1 paths, read-ahead, prewarm, keyframe index, frame cache, audio clock (plain mix), single- and multi-track **cuts-only** editing (move/trim/split/delete, snapping, J/K/L with real reverse), timeline with thumbnails and waveforms from `nle-cache`, drag-and-drop import | Single 1080p60 and 4K60 playback: 0 late frames over 10 min; scrub gates from §0.1; timeline with 1,000 clips within the UI gate; 0 CPU-copied bytes on hardware paths | effects, text, export |
| **3: Compositor + effects v1 + motion** | Render graph; linear fp16 colour pipeline; transforms, opacity, crop, flips, masks (shape and image, feather, corners), chroma key, eq/hue/RGB/LUT, blur, sharpen, spatial denoise, shadow, backdrop; keyframes for every animatable parameter with easing; on-viewer gizmo, crop and region tools | `bench-4x1080p60`: 0 late frames over 10 min on reference machines; next-frame parameter gate; golden images for every effect at 3 resolutions | export, captions |
| **4: Audio engine parity** | Per-clip gain, speed (stretch), mute/solo, filter chain, ducking, loudness, meters, voiceover recording/punch-in | 0 underruns with 8 tracks plus effects; drift < 1 frame/hour; offline == preview | audio stems, surround |
| **5: Export** | Render queue; encoders from Spike 5 with fallback chain; containers, chapters/markers, soft subs; size estimate; notifications/taskbar progress | Export fps ≥ HandPeg v2.6.3 baseline × 1.5 on the same project **[proposed]**; preview==export golden (PSNR ≥ 50 dB); bit-exact audio | GIF/WebP, render cache |
| **6: Text, captions, subtitles, AI** | Text layers; caption track and styles (Karaoke Sweep, Pop); caption editing in the timeline (word lane); SRT/VTT/ASS-text import; bitmap subtitles rendered in export; whisper.cpp in-process (Vulkan/CPU, optional CUDA) + VAD + model manager; captions-on-render; dead-air detection | Caption rendering adds ≤ 0.5 ms GPU at 1080p; transcription ≥ HandPeg's `whisper.exe` speed on the same model and hardware | styled text runs |
| **7: Productivity parity** | HUD templates; templates and `.hpstyle` importer; `.hproj` v2/v3 importer; backdrop/vertical templates; "new sequence from clips" with transitions; markers; proxies with tiers; yt-dlp import; autosave versions and crash recovery; GIF/WebP | Proxy generation never breaks the UI gate; HandPeg sample projects import and render matching HandPeg's export by eye (documented differences: linear blending) | render cache, HDR |
| **8: Distribution + polish** | Velopack (Windows installer, Linux AppImage), updater UI, first-run model prompts, settings search, shortcut editor, theming, AccessKit pass, backup export/import, docs | Release pipeline green on a tag; update round-trip tested on both operating systems; cold launch gate still holds on the installed build | macOS, HDR projects, plugins, multicam, nested sequences |

Feature parity with HandPeg's editor is the end of Phase 8. The segment render cache, HDR, nested sequences
and macOS are post-parity.

---

## 7. Draft `CLAUDE.md` for the new repository

````markdown
# <Name>: GPU-first NLE

## What it is
A non-linear video editor for Windows and Linux. One render path (a wgpu compositor in WGSL) draws the
preview and produces the export. FFmpeg (libav*, linked dynamically, LGPL build) is used ONLY to demux,
decode and encode. Hardware decode lands in GPU textures; hardware encode reads GPU textures.

## Architecture rules (non-negotiable)
1. **No FFmpeg filters for video effects.** libavfilter is not linked. Every visual effect is a WGSL pass
   in `nle-render`. libswscale only normalizes odd CPU pixel formats before upload.
2. **One render path.** Preview and export call the same `RenderGraph::evaluate`. Differences are limited
   to the knobs in `RenderQuality` (resolution scale, proxy use). Adding a preview-only shortcut is a bug.
3. **Parameters are uniforms.** A parameter change never rebuilds a pipeline, never reopens a decoder,
   and is visible on the next presented frame.
4. **Never block the UI thread.** No file I/O, no FFmpeg calls, no waiting on GPU fences, no locks held by
   workers longer than ~50 µs. Workers publish results through channels or `ArcSwap`.
5. **All document edits are `Command`s** applied through `Document::apply`. The model's fields are
   private to `nle-model`. Every command is undoable. Interactive drags are transactions (one undo step).
   Evaluation (rendering, playback) never writes to the model.
6. **Time is `nle_time::Time` (i64 flicks) and `Rational`.** No `f64` seconds in the model or in any
   comparison of positions. Conversions from media time bases use the helpers in `nle-time` only.
7. **Colour:** decode → linear Rec.709 `Rgba16Float` premultiplied; each effect declares its working
   space; output transform encodes for display or for the encoder. Never blend in gamma space by accident.
8. **No main video.** Every picture and sound is a clip; all clips are equal.
9. **GPU interop lives only in `nle-gpu`; FFI only in `nle-media` and `nle-ai`.** All other crates are
   `#![forbid(unsafe_code)]`. Every `unsafe` block has a `// SAFETY:` comment.
10. **One swapchain.** Never embed a foreign window or a second native surface. Overlays are drawn in the
    viewer pass with the compositor's geometry module.
11. **Terminology:** "keyframe" = animation key only. Codec intra frames are "I-frames". "Clip", "track",
    "sequence", "media" as defined in `nle-model` docs.

## Performance budgets (gates; see bench/gates.toml)
| Metric | Gate |
|---|---|
| Cold launch → first interactive frame | p95 ≤ 1.0 s (first run after install ≤ 1.8 s) |
| UI CPU frame time (reference project) | p50 ≤ 1.0 ms, p99 ≤ 2.0 ms (desktop); p99 ≤ 3.0 ms (laptop) |
| Input-to-photon | ≤ 2 refreshes at 60 Hz, vsync on |
| Parameter change → presented | ≤ 1 refresh p95 |
| Scrub to exact frame | intra ≤ 1 refresh; GOP≤15 1080p ≤ 3 refreshes; long-GOP nearest ≤ 1 refresh, exact ≤ 150 ms |
| bench-4x1080p60 playback | 0 late frames, 0 audio underruns over 10 min |
| CPU bytes copied per HW-decoded frame | 0 |
| Idle RSS, no project | ≤ 200 MB |
| Preview vs export frame (pre-encode) | PSNR ≥ 50 dB |
A change that regresses any benchmark by more than 5 % against the last phase tag needs a written reason in
the phase history file.

## Code style
- Rust 2024 edition, stable toolchain pinned in `rust-toolchain.toml`. `cargo fmt`; `cargo clippy -D warnings`
  with the workspace lint set in `Cargo.toml`.
- No `unwrap()`/`expect()` outside tests, except for documented invariants.
- Errors: `thiserror` in libraries, `anyhow` only in `nle-app` and tools.
- Logging and profiling: `tracing` spans on every frame stage and job; perf gates read these spans.
- Serialization never depends on locale; numbers in files are written by serde.
- Dependencies: pinned in `Cargo.lock`; every new crate is recorded in `docs/dependencies.md` with its licence
  and why; `cargo deny check` must pass (no GPL/AGPL unless the licence decision allows it).
- wgpu, egui, ffmpeg-sys versions are pinned. Upgrades are their own commit with a note in the phase
  history. API notes for the pinned versions live in `docs/api-notes/` (models are trained on older APIs:
  check there before writing wgpu/egui code).

## Testing and benchmarks
- Unit tests for `nle-time` and `nle-model`; property tests (proptest) for command/undo/serialization
  round-trips; fuzz targets for importers (subtitles, projects, .hpstyle).
- Golden-image tests for every effect (headless wgpu; lavapipe in CI; tolerance per test).
- Audio golden tests: offline render bit-exact against stored results.
- `bench` (release build, reference machines): startup, UI frame time, playback (late frames), scrub,
  export throughput, memory. Results are written to `bench/results/<phase>.json`.
- After every multi-file edit: `cargo build --workspace` and `cargo test --workspace` must pass with zero
  warnings.
- To check UI changes, run the app (`cargo run -p nle-app --release`) so the user can look at it. Do not
  claim a UI change works without running it.

## Workflow
- Work happens in phases. Each phase writes `docs/history/NNNN-phase-name.md` (four-digit sequence) with:
  **What** was built, **Why** (decisions and rejected alternatives), **Measured performance** (numbers copied
  from `bench/results/<phase>.json`, machine names, driver versions), **Known gaps**, **Deferred**.
- A phase finishes ONLY through `scripts/finish-phase <N> <phase-name>`, which:
  1. refuses to run with uncommitted changes outside the phase's scope, or with any file > 1 MB or any
     binary (exe, dll, so, mp4, mkv, wav, bin, zip...) staged;
  2. runs `cargo fmt --check`, `cargo clippy -D warnings`, `cargo test --workspace` (debug and release);
  3. runs `cargo deny check`;
  4. runs `bench` gates for the phase and writes `bench/results/phase-<N>.json`;
  5. checks that `docs/history/NNNN-<phase-name>.md` exists, has the five sections, and quotes the
     benchmark numbers;
  6. commits, creates the annotated tag `phase-<N>`, and pushes the branch and the tag.
- GitHub Actions builds Windows (x86_64-pc-windows-msvc) and Linux (x86_64-unknown-linux-gnu, AppImage) on
  every `phase-*` and `v*` tag, runs the correctness tests (no GPU benchmarks in CI), and attaches the
  binaries to the GitHub release. FFmpeg and whisper.cpp are built by CI and cached; **no binaries are ever
  committed to git**.
- Never commit, tag or push except through `scripts/finish-phase` or when the user asks.
````

---

## 8. Squisher companion app

**What it is:** a small tray app (target idle RSS < 50 MB **[ESTIMATE]**) that turns a clip into a file under
a size limit and puts it where you need it. Its core is HandPeg's `SocialSquisher` maths:
bitrate = `(target − offset) × 8192 / seconds − audio`, encoder order NVENC > QSV > AMF > libx264, height
chosen by bitrate, re-encode lower if the result is over size. That core moves into a shared crate on top of
`nle-media`. Note that Discord's free upload limit went **from 10 MB to 20 MB on 14 Aug 2026**, so re-check
the preset table (the changelog already warns preset sizes "may need correcting").

**(a) Watching clip folders with drag-out toasts: feasible, with platform caveats.**

- **Watching:** `notify` (ReadDirectoryChangesW / inotify). OBS's output path can be read from its profile
  (`basic.ini`). For NVIDIA's recorder and Medal, let the user pick the folder: their config locations are not
  something I verified, and they change.
- **Detecting a finished file:** size stable for N seconds, the file opens without sharing violations, and the
  container probes correctly (MP4 `moov` present, MKV cues). OBS writes recordings progressively; the replay
  buffer writes once.
- **"Toasts" that you can drag out of cannot be native notifications.** Windows Action Center toasts and
  freedesktop notifications are not drag sources. They must be your own topmost, no-activate, borderless
  windows, which you draw (egui is fine) and from which you start an OS drag:
  - **Windows:** `DoDragDrop` with `CF_HDROP` (HandPeg already does this in WPF; from Rust, the `drag` crate
    or the `windows` crate).
  - **Linux X11:** fine (XDND).
  - **Linux Wayland:** clients can't place windows at screen positions or keep them on top without
    `wlr-layer-shell`, which KDE and wlroots compositors support and GNOME does not. On GNOME, fall back to a
    native notification with "Squish" and "Copy" actions that opens the shelf window.
  - Drag-out from winit is not built in (see §2.4): budget platform code.

**(b) Squish to the clipboard as a file, for Ctrl+V into Discord: feasible on Windows, unverified on Linux.**

- **Windows:** put `CF_HDROP` plus `Preferred DropEffect = COPY` on the clipboard. Pasting into apps that
  accept file pastes uploads the file. Electron maps `text/uri-list` to `CF_HDROP`, and Discord is Electron.
  **I did not find documentation that Discord's paste handler accepts it; test it in a 1-day spike.**
- **Linux:** offer `text/uri-list` and `x-special/gnome-copied-files`. A Discord support thread says the Linux
  client pastes only `image/png`; it is old, but treat Linux paste as **unlikely until tested**.
- **Lifetime:** the file must outlive the paste. HandPeg deletes the squished file when its dialog closes,
  which must change: keep outputs in a squish folder with a time-to-live (for example 24 h) and clean up on
  start. On X11/Wayland the app must stay running to serve the clipboard (clipboard managers often don't keep
  file lists).

**(c) Edge drop shelf: feasible on Windows and X11, limited on Wayland.**

- A thin topmost tool window at a screen edge that expands when a drag hovers it (it is a drop target, so no
  global mouse hooks: low-level hooks are what anti-cheat and AV software dislike). Drop a video → it is
  squished → the item becomes draggable out and copyable.
- Wayland: same positioning limits as (a); KDE/wlroots via layer-shell, GNOME gets a normal window.

**(d) Why not inject into Discord's own drag-and-drop: don't.**

- **It violates Discord's Terms of Service** (client modification; that is how BetterDiscord-style mods are
  treated), putting the user's account at risk. Your app would be the reason.
- **It is code injection into another process** (DLL injection or patching Discord's Electron `asar`). AV and
  EDR products flag exactly that behaviour. Expect SmartScreen reputation damage and false-positive
  detections on your installer, which would hurt the editor too if they share a publisher.
- **It breaks constantly.** Discord's client updates very frequently, and its internals are not an API. Every
  update is a potential crash inside someone else's process.
- **Security and liability:** code inside the Discord process runs next to the user's session token. Even
  with good intentions you would be a token-stealer-shaped program, and any bug is your security incident.
- **Anti-cheat:** the target users are gamers. Injection tools running beside kernel anti-cheat (Vanguard,
  EAC, BattlEye) invite flags. Not worth explaining to users why they were kicked.
- **It is unnecessary.** OS drag-and-drop and the clipboard are the public contract every app accepts. If you
  want "one click to Discord", the legitimate route is a **user-supplied Discord webhook URL** (post the file to
  their own server channel), which is a documented API.

**Recommendation:** a separate binary in the same Cargo workspace (shared `nle-media` and squish core),
released with its own version and tag prefix. Build it after Phase 5 of the editor, when the encoder path
exists.

---

## 9. Questions that change the plan

1. **Licence.** Open source (GPL acceptable?) or closed? This decides x264/x265 (GPL), Rubber Band (GPL or
   commercial), how FFmpeg is configured, and whether `cargo deny` allows GPL. HandPeg's repository has no
   licence file at all.
2. **Reference hardware.** Which machines can you test on (GPU models, driver versions, laptop/iGPU), and which
   Linux distributions? The gates and Spike 1's matrix need named machines; cells you can't test stay
   unverified.
3. **Minimum supported GPUs and drivers.** HandPeg went out of its way for GTX 10-series with old drivers
   (FFmpeg 8.1 build switch). FFmpeg 9.0 dropped NVIDIA SDK < 11.1, and FFmpeg's Vulkan hwcontext needs Vulkan
   1.3. Is "Vulkan 1.3 + a 2023-or-newer driver" acceptable as the floor?
4. **Windows backend.** If Spike 1 shows Vulkan interop is worse on Windows (Intel especially), are you
   willing to ship the DX12 backend on Windows (same WGSL), or is Vulkan on Windows a hard requirement?
5. **HDR.** Is SDR-only acceptable through parity (HDR sources tone-mapped, no HDR export)?
6. **HandPeg import.** Do you need `.hproj` (v2/v3) and `.hpstyle` import? About one phase-week, and it pins
   down some semantics (track-shared look, blending space).
7. **Track vs clip properties.** HandPeg shares a track's position, look and filters across its clips. NLE
   convention is per clip (plus track effects). Which do you want?
8. **Editing scope for parity.** Ripple/roll/slip/slide tools, multiple sequences per project, nested
   sequences, multicam: in or out before parity?
9. **macOS.** Explicitly out? (wgpu's Metal backend makes it possible; VideoToolbox interop would be another
   spike.)
10. **Linux packaging.** AppImage only (Velopack) or also Flatpak? Flatpak's sandbox complicates GPU and video
    access, arbitrary file access and running yt-dlp.
11. **Transcription builds.** Ship a CUDA whisper build too (large CUDA runtime, NVIDIA only) or Vulkan + CPU
    only?
12. **Subtitle Editor.** OK to fold it into the main timeline (word lane plus inspector) instead of a separate
    window with its own player?
13. **Squisher.** Same repository and workspace, or separate? Same language?
14. **The "typical 4-layer project".** My proposed definition: 4 × 1080p60 H.264 8-bit 4:2:0 at ≤ 20 Mbit/s with
    GOP ≤ 120, each with a transform + opacity + one effect (key or blur), one text layer, 4 stereo audio clips
    with ducking. Is that your typical project, or are your sources OBS HEVC/AV1 at higher bitrates?

---

## 10. Sources (checked 2026-10-11)

- wgpu releases (v30.0.1; v30 `create_texture_from_hal` `initial_state`; NV12/P010 plane copies): https://github.com/gfx-rs/wgpu/releases
- wgpu `ExternalTexture` (docs for 30.0.1): https://docs.rs/wgpu/latest/wgpu/struct.ExternalTexture.html
- wgpu #9801 (Vulkan DMA-BUF import is single-plane): https://github.com/gfx-rs/wgpu/issues/9801
- wgpu-native #621 (no C API for D3D12 shared handles): https://github.com/gfx-rs/wgpu-native/issues/621
- filmcraft PR #152 (D3D11 NV12/P010 → wgpu DX12 zero-copy pattern): https://github.com/storytold/filmcraft/pull/152
- mpv-engine PR #5 (`texture_from_raw` + DropCallback): https://github.com/ophymx/mpv-engine/pull/5
- wgpu-external-frame (DMA-BUF / IOSurface / Windows shared handles): https://github.com/water-rs/wgpu-external-frame
- 2026 write-up on wgpu video upload and zero-copy: https://ginokent.github.io/en/posts/2026-03-04-wgpu-video-playback-pipeline/
- wgpu-hal Vulkan Adapter (`device_from_raw`): https://wgpu.rs/doc/wgpu_hal/vulkan/struct.Adapter.html
- wgpu-hal semaphore list (internal timeline semaphores): https://wgpu.rs/doc/src/wgpu_hal/vulkan/semaphore_list.rs.html
- FFmpeg `hwcontext_vulkan.h` (shared device, extensions, Vulkan 1.3): https://ffmpeg.org/doxygen/trunk/hwcontext__vulkan_8h_source.html
- FFmpeg `hwcontext_d3d11va.h` (array textures for decode): https://code.ffmpeg.org/Traneptora/FFmpeg/src/branch/master/libavutil/hwcontext_d3d11va.h
- FFmpeg 6.1 Vulkan decode: https://www.phoronix.com/news/FFmpeg-6.1-Released
- FFmpeg 7.1 Vulkan H.264/HEVC encode: https://phoronix.com/news/FFmpeg-7.1-Released
- FFmpeg 7.1 D3D12VA HEVC encoder: https://jbkempf.com/blog/2024/ffmpeg-7.1.0 and https://ffmpeg.org/pipermail/ffmpeg-devel/2024-April/325692.html
- FFmpeg 8.0 (Vulkan AV1 encode, VP9 decode, compute codecs): https://lwn.net/Articles/1034813/ and https://www.phoronix.com/news/FFmpeg-Vulkan-AV1-Encoding
- FFmpeg 8.0 whisper filter: https://ffmpeg.org/doxygen/8.0/af__whisper_8c.html
- FFmpeg 8.1 (Vulkan ProRes/DPX, D3D12 H.264/AV1 encode): https://phoronix.com/news/FFmpeg-8.1-Released and https://linuxiac.com/ffmpeg-8-1-brings-vulkan-compute-codecs-and-new-decoder-support/
- FFmpeg 9.0: https://www.phoronix.com/news/FFmpeg-9.0-Released and https://9to5linux.com/ffmpeg-9-0-lei-open-source-multimedia-framework-officially-released
- Vulkan Video driver status (Igalia, updated 2026-05-22): https://blogs.igalia.com/vjaquez/vulkan-video-status/
- Khronos AV1 decode / H.264/H.265 encode announcement: https://www.khronos.org/blog/khronos-releases-vulkan-video-av1-decode-extension-vulkan-sdk-now-supports-h.264-h.265-encode
- Intel ANV Vulkan AV1 decode (Linux): https://www.phoronix.com/news/Intel-ANV-Vulkan-AV1-Decode
- FFmpeg licence notes: https://doc.qt.io/qt-6.10/qtwebengine-3rdparty-ffmpeg.html and https://github.com/emveepee/libmpv-mingw64-builder/wiki/License
- Rust FFmpeg bindings: https://crates.io/crates/ffmpeg-next , https://crates.io/crates/ffmpeg-sys-next , https://crates.io/crates/rsmpeg , https://lib.rs/crates/rusty_ffmpeg , https://dev.to/yeauty/the-state-of-media-processing-in-rust-2026-what-each-crate-actually-covers-2k2c
- gpu-video / vk-video (Vulkan Video + wgpu, H.264): https://docs.rs/gpu-video/latest/gpu_video/
- Poppy (zero-copy decode into wgpu, self-reported): https://github.com/kuntal-devrat/poppy
- whisper.cpp Vulkan Windows binaries request (closed, not planned): https://github.com/ggml-org/whisper.cpp/issues/3673
- egui docs (performance notes): https://docs.rs/egui/latest/egui/
- egui #7761 (wgpu vs glow startup and latency): https://github.com/emilk/egui/issues/7761
- egui multi-viewport regression #8610: https://github.com/emilk/egui/issues/8610
- egui_dock 0.21 (egui 0.36): https://docs.rs/crate/egui_dock/0.21.0
- Qt `QRhiTexture::createFrom`: https://doc.qt.io/qt-6/qrhitexture.html
- Qt Multimedia FFmpeg texture converter: https://contribute.qt-project.org/doc/d5/d13/classQFFmpeg_1_1TextureConverterBackend.html
- Avalonia custom rendering / GPU interop: https://docs.avaloniaui.net/docs/graphics-animation/custom-rendering and https://github.com/AvaloniaUI/Avalonia/issues/9925
- Signalsmith Stretch (MIT) via Qt attribution: https://doc.qt.io/QT-6/qtmultimedia-attribution-signalsmith-stretch.html ; Rust bindings: https://docs.rs/ssstretch
- Rubber Band licence: https://breakfastquay.com/rubberband/license.html
- Velopack Rust and Linux packaging: https://docs.rs/velopack and https://docs.velopack.io/packaging/operating-systems/linux
- `drag` crate (outgoing drag; Linux via GTK): https://docs.rs/crate/drag/0.3.0
- winit Wayland drop report (third-party): https://zff.dev/SoulCrafted/Slicer/issues/146
- Electron clipboard (text/uri-list ↔ CF_HDROP): https://electronjs.org/docs/latest/api/clipboard-item
- Discord Linux paste limitation (old thread): https://support.discord.com/hc/fr/community/posts/360043164391-Allow-pasting-of-mime-types-other-than-image-png
- Discord free upload limit 20 MB (Aug 2026): https://www.gadgetreview.com/?p=402010 and https://neowin.net/news/discord-raises-file-size-upload-limit-for-its-non-subscribers/
