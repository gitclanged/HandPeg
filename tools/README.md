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

## GoldenTests.ps1 (golden-file tests of the graphs, the project format and the runner)

`pwsh -NoProfile -File tools\GoldenTests.ps1 [-Update] [-Filter <part of a name>] [-NoBuild]`

Builds `tools\GoldenTests` (a console program referencing the app's build, so it tests the very code HandPeg runs)
once, then runs it; the tests themselves take about 0.4 s. Each case writes a text file and compares it with its
golden file in `tools\GoldenTests\golden`; a difference is printed as a line diff (`-` golden, `+` now). `-Update`
writes the golden files anew from what the code makes now: after a change meant to change the graphs, run it and
review the golden files' diff in git before committing them (that diff is the review). Close HandPeg first: the build
writes `HandPeg.dll`.

What is covered:

- **FilterGraphBuilder** (`GraphCases.cs`, timelines written in code with neutral paths such as `D:/media/clip1.mp4`;
  the builder never reads files): cuts (`PlanCutInputs`), video, picture and text layers, effect cards in every zone,
  keyframes with every easing about an off-centre anchor point, fades and transitions (dissolve, wipe, slide, zoom,
  blur dissolve), crossfades on a track and across tracks, the live graph after a seek with sound (the layers' `amovie`
  and the main video's tracks) with proxies off and on, scrubbing, Render Preview and the composed timeline proxy.
  `Scene.Compile` makes the builder's state as `MainViewModel.CompileExportState` does, and calls the view model's own
  preparation of the copies (`LayLaterClipsOver`, `ApplyCrossfades`, `PairLayerCrossfades`) by reflection: when one is
  renamed, the tests say so.
- **The A/V invariant** (`AvCheck.cs`, `Timing.cs`), checked by code on every export without cuts and every live graph
  with sound: for each video clip with sound, a moment of its file is followed through the graph by its time stamps,
  the picture from its `movie=` to the overlay that lays it on the canvas, the sound from its `amovie=` to the `amix`;
  both must land where the timeline puts that moment (the clip's start plus the distance into it at its speed), within
  2 ms. A failure names the moment, where it is seen and heard, and which is late.
- **The .hproj format** (`ProjectTests.cs`): a format 2 file (`fixtures\format2.hproj`) read and written as format 3,
  read again and compared field by field; a HandPeg 1.x project (`fixtures\format1.txt`, written by hand in the 1.x
  layout) brought over; a newer format refused, opened only when asked and never written over; files that are not
  projects refused; seek indexes written and taken up again.
- **FfmpegRunner** (`RunnerTests.cs`): every fixture's command and 4,000 random argument lists of the characters
  quoting gets wrong, quoted by CliWrap and split again as FFmpeg splits its command line (`CommandLineToArgvW`), come
  back unchanged; how a command's graphs are given to each kind of FFmpeg in files.

The timing model (what each filter does to time stamps) was checked against FFmpeg 8 on an MPEG-TS file whose clock
starts at 1.4 s: `movie=` and `amovie=` give the file's own time stamps (`trim` / `atrim` cut on them), `sp=` and
`seek_point=` count from the container's start, a picture's seek lands on an I-frame, and a sound's seek lands on a
packet at or before its aim that only the demuxer knows (aimed at 18.4 s, it landed at 18.08 s), so the check reports
a sound seek aimed past its cut on its own rather than guessing the loss. An export of a fixture-like graph, measured
with a flash and a beep at the same moment of the file, put them where the model said, to a frame (overlay shows a
layer running faster than the canvas from the next canvas frame when their time stamps meet).

The fixtures hold nothing from a real computer: no user's paths, names or media. Keep it so; the builder needs no real
files, and the project tests make the few files they need in a folder of their own under `%TEMP%`.

## AvSyncTest.ps1 (is the player's sound in step with its picture?)

`pwsh -NoProfile -File tools\AvSyncTest.ps1 -Project <file.hproj> [-Points 100,300,600,1500] [-Proxies off|on|both]
[-MainProxy <file>] [-Libmpv <path>] [-Tolerance 15] [-Session] [-NoBuild]`

Builds `tools\AvSync` (a console program referencing the app) once, opens the project in the app's own view model with
no window, and for each moment builds the live graph the app sends the player with a seek there (sound laid out from
that moment, the files' starts and I-frames, the proxies in use: `MainViewModel.BuildLiveFilterGraph`). libmpv plays it
headless from that moment in its encoding mode (`o=`, `of=matroska`, `ovc=ffv1`, `oac=pcm_s16le`, `orawts`), so what it
wrote keeps the player's own time stamps. Then:

- **the sound** is found in the file it came from by cross-correlation, to the sample (a score of 1.000 is a perfect
  match), and its lateness is where it was found against where the timeline puts it;
- **the picture**'s lateness is read from its graph's time stamps, and checked against which of the file's frames each
  output frame really shows (the range in brackets: every frame allows it);
- **A/V** is the difference, PASS within the tolerance.

`-Proxies on` switches Per-Clip Video Proxies on in that process (the proxies already made are used; none is made);
`-MainProxy` names a proxy of the main video to stand for one. `-Session` plays the steps the app takes within a session
in real time instead: a seek while paused with the quick graph, the graph with sound 300 ms later, Play; a seek while
playing; a graph rebuilt while playing (`--rebuild-from`, default 151.5 s). There mpv's clocks are read (`audio-pts`
against `time-pos`, compared with plain playback of the main video, whose frames are half as long), with the time from
each seek to playback restarting.

In encoding mode mpv takes neither a seek nor a new graph, so each moment is a fresh start with `start=` and the graph
given before playback: the same thing the app's "seek, then its graph" gives (mpv builds a new graph afresh and runs it
from the seek's target). Starting exactly on some moments (20.000 s and 40.000 s in the Pass 4.1A test project) makes mpv's encoder
refuse to start ("Encoder trying to write packet before muxer was initialized"): pick a moment a few milliseconds off.

Check the harness on a known answer when changing it, running `tools\AvSync\bin\Debug\net8.0-windows\AvSync.exe test
--project <file> --points 300 --proxies off` with `--audio-shift-ms 100` (the whole sound 100 ms late: must read +100 ms)
or `--video-shift-ms 50` (every video layer 50 ms late: +50 ms). The project is only read, and `appsettings.json` is
put back byte for byte when the harness ends.

## StartupBench.ps1 (how long does a launch take, as a user sees it?)
`powershell -NoProfile -File tools\StartupBench.ps1 -Builds "<baseline>\HandPeg.exe;<candidate>\HandPeg.exe" [-Rounds 5] [-Seconds 8] [-Warm]`

Launches each build in turn through `explorer.exe` (a user's launch: its start-up feedback, its foreground rules) and
reads, per launch, the "window drawn N ms after the process started" line HandPeg logs on every start, and how long
Windows showed the busy pointer (polled every 20 ms while the build runs). Builds alternate, so a busy computer (an
encoder, a recording) affects all of them alike: compare only numbers from one run. Each build is closed through UI
Automation, and `%APPDATA%\HandPeg\appsettings.json` is backed up first, put back after every launch and restored at
the end (hash checked). Close HandPeg first.

- Compare **Release** builds (`dotnet publish -c Release -r win-x64 --self-contained false -p:DebugType=none -o
  <folder outside the repo>`): a Debug build compiles everything at run time.
- `-Warm` launches each build once first, unrecorded: a new build's first start records its multi-core JIT profile.
- Validated on 2026-10-25 against the build before the sentinel fix: 5,566 ms busy pointer against 146 ms.
- Every launch asks GitHub's API: the app's own update check at start (Settings, Check for App Updates on Startup) and,
  when the build runs 30 s or more, the dependency check. About 60 launches in an hour reach GitHub's anonymous limit;
  HandPeg's checks then log "hourly limit" for up to an hour. Keep `-Seconds` under 30 and the rounds modest.
