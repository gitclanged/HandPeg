# HandPeg

HandPeg is a featherweight, hardware-accelerated video editor and encoder for Windows, built for getting from a raw
gameplay recording or screen capture to a finished highlight, short or upload as fast as possible.

It has no middleware: every edit compiles straight to an FFmpeg filter graph, played live through libmpv on the GPU and
encoded by FFmpeg on export. The window paints in well under a second and HandPeg idles under 200 MB of memory.

## Two modes
- **Editor Mode:** a multi-track timeline with video, image, text and audio layers, a live preview of the finished
  picture and sound, effects, keyframes, fades and transitions.
- **Encoder Mode:** a clean batch converter and downloader (yt-dlp): open a file or a link, trim with cut segments,
  pick a preset, export or queue it.

## What it does
- **Live preview that keeps up:** seeks land inside each file at the nearest I-frame (remembered per file and in saved
  projects), so long timelines and long recordings play and scrub at once, with the sound in step with the picture.
- **Smart proxies:** heavy files (above 1080p, AV1, VP9, ProRes, 10-bit HEVC) get a small editing copy made in the
  background, for the player only; exports always read the originals. An optional composed proxy of the whole timeline.
- **Effect stacks in zones:** each clip's effects are cards, applied in order: key, mask, color, blur, blend modes,
  adjustment layers and a Sequence Output stack, each animatable over its own time range. Copy a stack and paste it
  whole, onto a whole track, or just the cards you pick.
- **Edit right on the video:** click a layer on the preview to select it, then drag, resize, turn it or move its
  anchor there; drop shadows with distance, angle, softness and colour.
- **Keyframe lanes and curves:** a lane per property with a graph of the value over time, easings from Hold to Bounce
  and Custom Bezier, and an anchor point that layers turn and scale about. Select keyframes across rows to move, ease,
  copy or delete them together.
- **Fades and transitions at a clip's corners:** drag a clip's top corners to fade its picture and sound; dissolves,
  wipes, slides, zooms and blur dissolves, crossfades on one track or across tracks.
- **Dockable layouts:** every part of the window is a pane you can dock, tab together or move to a side window. Built-in
  layouts (Standard Editing, Color & Effects, Assembly & Media) and three of your own per mode, previewed live from the
  Layout button before you keep one.
- **Five themes:** Windows System, OLED Black, Leonardo, Reno and Reno Dark, chosen from the Layout button's Themes row.
- **One Export tab:** video, audio (codec, bitrate, sample rate, channels), output, chapters and the Save As / Start
  Encode / Add to Queue buttons in one place, with the progress of an export as a dimmed fill across the full width of
  the title bar.
- **Projects at a glance:** the Project Manager shows a picture of each project's video that scrubs under the pointer.
- **Auto-captions** with whisper.cpp, styled and previewed in the player, burned in on export.
- **Hardware encoders** (NVENC, AMF, Quick Sync) tested on your PC, with a software fallback.
- **Crash diagnostics without personal data:** a crash writes a small plain-text report to your PC with no file names,
  paths, user names or words from your media. Nothing is sent anywhere; attach a report to an issue to share it.

## Getting started
Download the installer (`HandPeg-win-Setup.exe`) or the portable zip from the
[Releases page](https://github.com/gitclanged/HandPeg/releases). On first start HandPeg downloads the engines it uses
(FFmpeg, libmpv, yt-dlp and, if you want captions, whisper.cpp), pinned to builds tested with this version, and keeps
them updated in the background. HandPeg updates itself from the same page. It needs Windows 10 or 11 (x64) and the
.NET 8 Desktop Runtime.

## License
MIT: see [LICENSE](LICENSE).
