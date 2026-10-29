# HandPeg

[![Latest release](https://img.shields.io/github/v/release/gitclanged/HandPeg?label=release)](https://github.com/gitclanged/HandPeg/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64%20%7C%20ARM64-0078D4)](#getting-started)
[![.NET 8](https://img.shields.io/badge/.NET-8%20WPF-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

HandPeg is a featherweight, hardware-accelerated video editor for Windows, built for getting from a raw gameplay
recording, stream VOD or screen capture to a finished highlight, short or upload as fast as possible.

There is no middleware: every edit compiles straight to an FFmpeg filter graph, played live through libmpv on the GPU
and encoded by FFmpeg on export. The window paints its first, final frame in under half a second, and HandPeg idles
well under 200 MB of memory.

![The HandPeg v4.4.0 editor: the newest recording dropped on the Overwatch vertical style, with the one Timeline, its child mini-stacks and the Overview Bar](docs/screenshots/workspace-v4.4.png)

![Hold Tab: X-Ray and the keyboard-and-mouse map over the player, naming what each key does](docs/screenshots/xray-keymap-v4.4.png)

![Settings > Keybinds & Lightning Dial: the dial's categories and the drawn keyboard-and-mouse map](docs/screenshots/settings-keybinds-v4.4.png)

<img src="docs/screenshots/dial-v4.4.png" alt="The Lightning Dial on Mouse 5: seven categories and Settings in one ring" width="280">

**New in v4.4.0:** one Timeline for every track, with a game style's HUD pieces and the captions as collapsible children
that follow their parent; captions anchored to the voice's words; each clip's own place, size, crop and blur fill; the
Lightning Dial on Mouse 5 and a rebindable left-hand key cluster (hold W/A/S/D to zoom and pan what is under the pointer);
hold Tab for X-Ray and a clickable key map; dead air that folds both ways; and undo history saved with the project.

## What it does
- **One multi-track timeline:** video, image, text and audio layers with a live preview of the finished picture and
  sound. Trim any clip from either edge (Shift for frame-by-frame without snapping, Ctrl to ripple), crossfade
  neighbouring clips on one track, split or trim at an exact timecode from a clip's menu. Splitting a keyframed clip
  keeps both halves' motion exactly, and cuts are just split points: what is on the timeline is what renders.
- **Game styles in one drop:** drop a gameplay video on a game's style and get a tall frame, the HUD pieces cut out with
  the game's mask, and a blurred background, all made from your video, even after you split or restyle its pieces.
- **Live preview that keeps up:** seeks land inside each file at the nearest I-frame (remembered per file and in saved
  projects), so long timelines and long recordings play and scrub at once, with the sound in step with the picture to
  the millisecond, and a paused playhead stays exactly on the frame or keyframe you put it on.
- **Smart proxies:** heavy files (above 1080p, AV1, VP9, ProRes, 10-bit HEVC) get a small editing copy made in the
  background, for the player only; exports always read the originals.
- **Effect stacks in zones:** each clip's effects are cards, applied in order: key, mask (with a Mask Offset to nudge a
  HUD mask), color, blur, blend modes, adjustment layers and a Sequence Output stack, each animatable. Copy a stack and
  paste it whole, onto a whole track, or just the cards you pick.
- **Edit right on the video:** select, drag, resize and turn layers on the preview; 3D drop shadows.
- **Keyframe lanes and curves:** a lane per property, easings from Hold to Bounce (each drawn as its own curve icon) and
  Custom Bezier, an anchor point, and multi-select to move, ease, copy or delete keys together.
- **Fades and transitions** at a clip's corners: dissolves, wipes, slides, zooms and blur dissolves, each with its icon.
- **Easy Social Export:** one-click cards for vertical shorts, a YouTube master, chat-size targets (under 10, 25 or 50 MB),
  a social feed HD file and a lossless archive, with every setting still open in Advanced Export. Copy YouTube chapter
  timestamps straight from your markers, with SponsorBlock segments cut out or kept as chapters.
- **Media browser:** posters for a whole folder in moments (one frame each first, the hover-scrub strips after), smooth
  middle-click scrolling, and drag-and-drop onto the timeline.
- **Dockable layouts:** every part of the window is a pane you can dock, tab together or move to a side window. Four
  built-in layouts (Standard Editing, Color & Effects, Assembly & Media, Vertical & Shorts Studio) and three of your
  own, previewed live from the Layout button; a right-click on any pane's tab holds that pane's settings.
- **Five themes:** Windows System, OLED Black, Leonardo, Reno and Reno Dark. Every icon and curve is drawn from
  geometry: no bitmap UI assets.
- **Auto-captions** with whisper.cpp; **downloads** with yt-dlp, signing in through your browser's cookies when a site
  asks for it.
- **Hardware encoders** (NVENC, AMF, Quick Sync) tested on your PC during setup, with a software fallback.
- **Crash diagnostics without personal data:** a crash writes a small plain-text report to your PC with no file names,
  paths, user names or words from your media. Nothing is sent anywhere.

## Getting started
Download the installer (`HandPeg-win-Setup.exe`) or the portable zip from the
[Releases page](https://github.com/gitclanged/HandPeg/releases). The first-run wizard lets you pick a theme and a
starting workspace, downloads the engines HandPeg uses (FFmpeg, libmpv, yt-dlp and, if you want captions, whisper.cpp),
pinned to builds tested with this version, and finds your GPU's hardware encoders. HandPeg updates itself from the same
page. It needs Windows 10 or 11 (x64) and the .NET 8 Desktop Runtime. On Windows on ARM, use the ARM64 portable zip: it
runs natively and downloads the ARM64 builds of its engines.

## Building from source
```
dotnet build HandPegApp.csproj
pwsh -NoProfile -File tools\GoldenTests.ps1
```
The repository:

| Path | What is there |
|---|---|
| `src/` | The program: `Program.cs`, `App.xaml`, `MainWindow` and its partials |
| `src/Controls/` | Drawn controls: timeline tracks, keyframe lanes, gizmos, the video surface, button chrome |
| `src/Dialogs/` | Windows and dialogs: first run, settings, Project Manager, layer and caption tools |
| `src/Docking/` | The dock manager, panes, tabs and title rows |
| `src/Views/`, `src/ViewModels/` | The Media, Effects and subtitle panes; the main view model and its partials |
| `src/Services/` | The filter graph compiler, the libmpv player, FFmpeg runner, probes, caches, background work, themes |
| `src/Models/` | Layers, effects and their registry, easing curves, transitions, presets, sequences |
| `tools/` | Golden tests, the A/V sync harness, the startup benchmark, the code index |

## License
MIT: see [LICENSE](LICENSE).
