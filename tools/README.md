# Developer tools

Scripts used to test HandPeg by hand-like input and to debug player graphs. Not part of the build.

## UiDriver.ps1 (drive the real app)

Dot-source it in PowerShell 7: `. .\tools\UiDriver.ps1`. It gives:

- `Shot <name>`: a screenshot of all monitors, scaled, saved under `%TEMP%\handpeg-ui`.
- `MoveTo`, `Down`, `Up`, `Click`, `Glide x0 y0 x1 y1`: real mouse input (a held `Glide` is a drag).
- `FindWindow '<title*>'`, `FindByName`, `Center`: UI Automation lookups. Match overlay targets by
  `ControlType.Button` and a non-empty rectangle: hidden copies of a name (in collapsed lists) sit at 0,0.

A real drag-and-drop test: open an Explorer window on a folder of test videos, find the file's
`ListItem`, `MoveTo` it, `Down`, `Glide` into the HandPeg window (the overlay appears over an empty
window), find the target button, `Glide` onto it, `Up`.

Bring HandPeg to the front with an Alt key tap before `SetForegroundWindow` (Windows refuses otherwise).
**Close HandPeg before `dotnet build`**: a running copy locks `HandPeg.dll`, the copy fails, and the next
launch runs the old build (watch for MSB3026/MSB3027 in the build output).

## MpvGraphTest.ps1 (does libmpv survive this graph?)

`pwsh -NoProfile -File tools\MpvGraphTest.ps1 -GraphFile graph.txt -Video <file> [-Seconds 4] [-Libmpv <path>]`

Plays the video headless in libmpv with the graph as `lavfi-complex` and prints `survived N s`, or dies
(a crash only takes the test process). Get a graph from the app with `HANDPEG_LOG_GRAPH=1`, which writes each
live graph to `%LOCALAPPDATA%\HandPeg-Data\logs\HandPeg.log`. libmpv's FFmpeg differs from the standalone
one: `gblur` in a mask chain crashed it (fixed in 3.0); test new filters here before using them live.

## hud_mask_check.py

`python tools/hud_mask_check.py Services/HudLibrary.cs <outdir>` draws each game's outlines from
`HudLibrary.cs` into a 1280 x 720 grey picture, to overlay on a screenshot with FFmpeg and check the tracing.
