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

## hud_mask_check.py

`python tools/hud_mask_check.py src/Services/HudLibrary.cs <outdir>` draws each game's outlines from
`HudLibrary.cs` into a 1280 x 720 grey picture, to overlay on a screenshot with FFmpeg and check the tracing.

## GoldenTests.ps1 (golden-file tests of the graphs, the project format and the runner)

`pwsh -NoProfile -File tools\GoldenTests.ps1 [-Update] [-Filter <part of a name>] [-NoBuild]`

Builds `tools\GoldenTests` (a console program referencing the app's build, so it tests the very code HandPeg runs)
once, then runs it; the tests themselves take under 2 s (about 1.3 s of it the timeline model's 30,000 splits and
frame loops). Each case writes a text file and compares it with its
golden file in `tools\GoldenTests\golden`; a difference is printed as a line diff (`-` golden, `+` now). `-Update`
writes the golden files anew from what the code makes now: after a change meant to change the graphs, run it and
review the golden files' diff in git before committing them (that diff is the review). Close HandPeg first: the build
writes `HandPeg.dll`.

What is covered:

- **FilterGraphBuilder** (`GraphCases.cs`, timelines written in code with neutral paths such as `D:/media/clip1.mp4`;
  the builder never reads files): 4.2.0s cuts baked into split points of the main video, video, picture and text layers, effect cards in every zone,
  keyframes with every easing about an off-centre anchor point, fades and transitions (dissolve, wipe, slide, zoom,
  blur dissolve), crossfades on a track and across tracks, Render Preview, the composed timeline proxy, and a
  short video repeating to the end (4.2.0's loop, read as one source). The fixtures are written in 4.2.0's terms (a
  `Scene`: the main video's clip and pieces, layers by track id); `Scene.Compile` makes the builder's state as
  `MainViewModel.CompileExportState` does, its timeline the `TimelineDocument` the test-only adapter
  `LegacyFixtures.cs` writes of the scene (4.3Bs conventions; the view model has held a document of its own since 4.3C); the builder lowers and
  prepares it itself (`FilterGraphBuilder.Timeline.cs`). `TimelineGraphCases.cs` writes documents by hand
  (`*-timeline-native*`): what only the document can say (gaps on Video 1, J/L cuts, slips, a picture in picture of the
  main video's own file under it, a crossfade on an Audio track).
- **The A/V invariant** (`AvCheck.cs`, `Timing.cs`), checked by code on every export with sound: for each video clip with sound on the timeline (the document's; an unlinked clip's sound where its slip
  puts it), a moment of its file is followed through the graph by its time stamps,
  the picture from its `movie=` to the overlay that lays it on the canvas, the sound from its `amovie=` to the `amix`;
  both must land where the timeline puts that moment (the clip's start plus the distance into it at its speed), within
  2 ms. A failure names the moment, where it is seen and heard, and which is late.
- **Its GPU side** (`gpu-av-invariant`, `AvCheck.Gpu.cs`; 5.0-Verify, in place of the live graphs' cases): on the same
  timelines (the exports', the long session from where its seeks went, its proxies read in the preview, the loop from
  30 s, the three document-made ones) at the same moments and as many into every clip of the main video, every layer
  `GpuScenePlan` draws from a file must be at the moment of it the timeline puts there and at the moment `AudioProgram`
  plays for its clip's sound (an unlinked clip's slip moved in; a HUD piece and the blurred background held to the main
  video's), within 0.5 ms. No worker and no file: the plan and the program are worked out. A repeating clip's sound is
  played once (in the export too): past its file's end the text says it is silent.
- **The .hproj format** (`ProjectTests.cs`, format 5 since 4.3C): a format 5 file (`fixtures\format5.hproj`, written by
  hand: two clips of the main video, a track of clips with keys, effects (an unknown one's own settings kept), a mask, a
  bin entry, the envelope) read and written, read again and compared field by field and against the fixture; a newer
  format (6) refused, opened only when asked and never written over; formats 1-4, files that are not projects and a
  format 5 file without a timeline refused; seek indexes of every clip's file written and taken up again, and masks
  carried without the document in memory being changed.
- **FfmpegRunner** (`RunnerTests.cs`): every fixture's command and 4,000 random argument lists of the characters
  quoting gets wrong, quoted by CliWrap and split again as FFmpeg splits its command line (`CommandLineToArgvW`), come
  back unchanged; how a command's graphs are given to each kind of FFmpeg in files.
- **The timeline document** (`TimelineDocumentTests.cs`, the model of v4.3, which the builder compiles since 4.3B), each
  case writing down what it did:
  - `timeline-frame-rate`: the NTSC fractions, frames and quantizing, and 10,000 splits at 59.94, 29.97 and 60 fps
    with every cut one exact double on its frame.
  - `timeline-edits`: the transaction's edits step by step on track 1 and the tracks above it (split, trims, ripple,
    roll, slip, J/L, moves across tracks, crossfades, gaps, locks, a new track, nesting, rollback), and the viewport
    search on 2,000 clips against a full scan.
  - `timeline-edge-cases`: file ends at 60, 59.94 and 23.976 fps, free (precision) edits, J/L limits, ripples held
    back by another track, track-only commits, refusals that leave nothing changed, and a rollback of every property.
  - `timeline-json-round-trip`: a document written with the project files' JSON options, read back and written again;
    the golden file shows the timeline's shape in format 5.
  - `timeline-folds` (4.3D): stretches folded out of every track and kept at their seams, a fold rolled back, the folds
    through the project file's JSON and a copy, then each put back, every track playing what it played before.
  - `timeline-split-keys` (4.3D): a clip split between two keyframes, then its second half again, for every easing: each
    half's keys, the key on each cut, its part of the curve as FFmpeg reads it, and no stray from the whole clip's motion.

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
[-MainProxy <file>] [-Tolerance 15] [-Seconds 3] [-Session] [-Audible] [-NoBuild]`

Builds `tools\AvSync` (a console program referencing the app, which starts itself again as the compositor's worker, as
HandPeg does) once, opens the project in the app's own view model with no window, and takes the state the GPU engine is
given (`MainViewModel.LivePreviewState`: the files' starts and I-frames, the proxies in use). For each moment it draws the
next few seconds with a real compositor worker (`GpuScenePlan`, every frame exact, read back: nothing on a screen) and
renders the same seconds of sound with the real mixer (`AudioProgram.Build`, `AudioMixer.Render` offline), and writes both
into one Matroska file on the sequence's clock (FFV1 and float PCM). Then:

- **the sound** is found in the file it came from by cross-correlation, to the sample (a score of 1.000 is a perfect
  match), and its lateness is where it was found against where the timeline puts it;
- **the picture**'s lateness is the moment of its file the scene plan gives the worker for the measured clip (found by
  `LayerInfoAt`) against where the timeline puts it, and is checked against which of the file's frames each output frame
  really shows (the range in brackets: every frame allows it; the layer's box and crop are the plan's);
- **A/V** is the difference, PASS within the tolerance (15 ms); nominal is within 0.5 ms.

The measured clip is the topmost video clip with sound playing at the moment, else the main video. `-Proxies on` sets the
clips whose files have a proxy made to Optimize Long-GOP Clip for Scrubbing in that process (none is made); `-MainProxy`
names a proxy of the main video to stand for one. The files are read back with `-copyts` and sought by their distance from
the container's start (`-seek_timestamp` with `-copyts` lost 0.78 s of an MPEG-TS file's sound).

`-Session` plays the steps the app takes in real time instead, through a real `GpuPreviewEngine` and its
`WasapiAudioEngine` on the sound device Settings name (muted unless `-Audible`; the clock runs all the same; with no device
the engine's own processor clock is measured): at each moment a seek while paused, the paused settle, Play; a seek while
playing 20 s on; twice the speed and back; then Loop's wrap (played into the end, and at the engine's `Ended` back to the
start and Play, as `MainWindow.OnPlaybackEnded` does). The engine's swap chain is on a window of the harness's own off every
screen (beyond the virtual desktop), shown without being activated. A thread follows both clocks every quarter of a
millisecond: the sound heard (`WasapiAudioEngine.Position`: IAudioClock's position and its QPC, carried forward, the Audio
Sync Offset added; IAudioClock is at the speakers, so the stream latency printed is not added again) and every frame the
pump presents (its scene's moment, from the shared memory's scene of the sequence the worker presented). When a frame
reached the screen is DXGI's frame statistics where it reports them (DWM may stop composing a window off every screen),
else that median latency after Present, else the first vblank after Present on DWM's grid (said in the report). A frame's
A/V is its moment minus the sound's then (positive: the picture is ahead); each step's median must be within 0.5 ms
(nominal) and no frame 15 ms or more off. `--dump <file>` writes every frame presented, for looking into a result.

Check the harness on a known answer when changing it, running `tools\AvSync\bin\Debug\net8.0-windows\AvSync.exe test
--project <file> --points 300 --proxies off` with `--audio-shift-ms 10` (the sound rendered 10 ms late: the audio column
reads +10 ms) and `--video-shift-ms 40` (the picture drawn 40 ms late: the video column +40 ms; A/V -30 ms). The project
is only read, and `appsettings.json` is put back byte for byte when the harness ends; set `HANDPEG_DATA_DIR` to a folder of
the run's own (with `deps\ffmpeg` in it) to keep the app's caches and settings out of it altogether.

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
