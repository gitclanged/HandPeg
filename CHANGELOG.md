# Changelog

Release notes for HandPeg, newest first. Notes for 2.1.0 and earlier are on the
[GitHub Releases page](https://github.com/gitclanged/HandPeg/releases).

## HandPeg v4.3.0

### User-Facing Highlights
- **Full-window Settings with a live preview stage.** Settings now fill the window: a category rail, search, a folded
  Advanced section, and a live preview stage that draws what each setting does as you change it, before you save: a
  miniature of the chosen theme, a stretch of timeline, the caches' gauges, the processors and graphics cards at work, a
  speech model's trade-off.
- **The Voice track finds itself.** When a video is opened, an audio track named for a voice (Mic, Microphone, Voice,
  Commentary, Dialogue, Host and the like; a name that is only that word first) is marked as the Voice track. Game, music,
  desktop and similar tracks never are, and chat tracks only as a last resort. Auto-Captions listen to the Voice track
  whenever one is marked, by hand or by name, and so does Remove Dead Air on the main video. It can be switched off in
  Settings > Auto-Captions, and unticking Voice on the Audio tab always wins.
- **Fold dead air away, and give it back.** Remove Dead Air's Split & Mark takes the silences out and closes the gaps,
  keeping each one at its cut: right-click the clip after a cut and choose **Restore Folded Silence** to put a word back, or
  **Restore All Folded Silences** when there are several.
- **Splits keep the motion exactly.** Splitting between two keyframes gives each half a keyframe on the cut and its exact
  part of the curve, for every easing, so neither half jumps or changes speed. Effect ranges across the cut are split too.
- **Drop media instantly, even while the Media pane is indexing.** A file dropped from the Media pane while it was making
  pictures of a folder used to take seconds to land; it now goes ahead of all of that (about a tenth of a second), and the
  Media pane's background work pauses while you drag.
- **Undo everywhere.** Ctrl+Z / Ctrl+Y (and Ctrl+Shift+Z) work in the Subtitle Editor (with Undo and Redo buttons; typing,
  drags, splits, additions and deletions each a step, and the whole visit one step in the main window afterwards), in Draw
  Custom Shape, HUD Layers from a Mask, the Filters / Transform / Mask windows, Audio Filters, Caption Style, Blur and Layer
  Properties, and in the Voiceover Studio. In the main window a number or search box no longer swallows them once you have
  moved on to something else.
- **Native Windows on ARM (ARM64).** `HandPeg-4.3.0-win-arm64.zip` runs natively (it needs the ARM64 .NET 8 Desktop
  Runtime) and downloads ARM64 builds of FFmpeg, libmpv, yt-dlp and whisper.cpp (Adreno OpenCL or CPU), each pinned to a
  checked build. This is its first release: it has not yet been tried on ARM hardware here, and it does not update itself.

### More New Features & Workflow Changes
- **Track 1 is a track like any other.** The main video's track has its own box on the Canvas and over the player, with the
  rotation handle and anchor point; each of its clips can have its own keyframes and effects. A game style's HUD pieces and
  blurred background stay on through every piece of your video, however you split or restyle it.
- **Live Preview is always on.** It comes on with every video, style or project you open; the button beside the playback
  controls turns it off for what is open. (Its start-up checkbox in Settings is gone.)
- **Remove Dead Air / Silence...** on the right-click menu of every video and audio track, and **Generate Auto-Captions...**
  on the audio tracks' menus and in the Add Layer menu.
- Render Preview starts at the in point while one is set, otherwise at the playhead (from the start when the playhead is at
  the end).
- Chapters at Cut Points: a chapter at every clip edge on any video track (edges within half a second count once).
- Presets and styles are put on the timeline as templates; a style chosen before any video keeps its place for the video
  (its layers under or over it as the style says, the video's look), and layers that ran to the end follow the video's end.
- `[` and `]` (and a right-drag on the timeline) split every unlocked track at both points; nothing is taken out by them.
  The Play Only Cut Segments option is gone: playback always shows the timeline as it is.
- The timecode ruler is always shown above the tracks, also on an empty timeline.
- The Project Manager and the launch window open at once, listing the projects a moment later.
- Opening a project and closing it again without touching anything never asks to save it.

### Under the Hood & Fixes
- **One timeline document (project format 5).** The whole timeline (every track, clip, look, fade and crossfade) is one
  model that the player, the export, undo and the project file all read. There is no cut list any more: cuts are split
  points, and what is on the timeline is what renders. HandPeg 4.3.0 opens format 5 only; formats 1 to 4 are refused with a
  message, earlier versions cannot open format 5, and there is no converter in HandPeg.
- Fixed: dropping a video on a style showed the plain video in the player, not the style's tall frame, whenever Live
  Preview was off.
- Fixed: a game style's HUD pieces and blurred background vanished over a piece of the main video given an effect of its own.
- Fixed: a style chosen before opening a video put the HUD pieces over the video and stopped them after 5 seconds.
- Fixed: hovering across a tile in the Media pane or the Project Manager slid the picture to the right (by up to a tenth of
  its width), with a sliver of the previous frame at its left edge; the hover strip is made for one tile at a time.
- Fixed: clicking a box on the Canvas or the player only to select it, or opening a layer's window and changing nothing,
  left an undo step that did nothing.

## HandPeg v4.2.0

### Highlights
- **One dedicated editor.** Encoder Mode, its top bar and the mode switch are gone from HandPeg: the window is the editor, with one set of panes and layouts. The header is four buttons (Add Media, Layout, Project, Settings), the spatial pane is now called **Canvas**, and a fourth built-in layout, **Vertical & Shorts Studio**, joins the other three. Saved workspaces carry over.
- **A setup wizard.** First run picks a theme and a starting workspace (each shown live), downloads the core engines (or core + local auto-captions), and always finishes knowing your GPU's hardware encoders. It also opens on a PC where both engines are missing.
- **Easy Social Export.** An Easy Social Export | Advanced Export switch on the Export tab: one-click cards for short-form vertical (1080 x 1920, 60 fps), a YouTube master, chat and Discord shares under 10, 25 or 50 MB, social feed HD and a lossless archive. Every value they set stays editable in Advanced Export.
- **Copy YouTube timestamps.** One click copies chapter lines for your markers, placed through every cut, with SponsorBlock segments cut out, kept as named chapters, or ignored.
- **Timeline trims.** Shift-drag trims or moves frame by frame with no snapping; Ctrl-drag ripples. The main video's pieces trim from both edges, and neighbouring clips on one track crossfade.

### New
- **Mask Offset X / Y** (-50 % to +50 %) on the mask card and in Layer Properties, to nudge a HUD mask without redrawing it.
- **Signed-in downloads:** a link behind a sign-in or age gate is tried again with your browser's cookies (Waterfox, Firefox, Edge, Chrome, Brave) or a cookies.txt file (Settings > Preview & Cache > Downloads).
- **Split / Trim by Exact Timecode...** on a clip's right-click menu, with I-frame suggestions.
- **Pane settings on the tab menu:** right-click a pane's tab for its settings (Media, Timeline, Player and Canvas, Effects and Keyframes) and a link to the rest.
- **Curve icons:** every easing in the Keyframes pane and every transition in the fade menus is drawn as a small icon of its own curve or motion.
- **Media pane:** middle-click auto-scroll; posters come in two passes, a single frame for every tile first and the hover-scrub strips after (at once for a tile you hover).
- Timeline height by Ctrl-dragging the border beside it, or Compact / Medium / Tall from the Timeline tab's menu.

### Changed
- Reno and Reno Dark: sunken check boxes with crisp ticks, one classic dotted focus rectangle, raised flyouts and menus; dialogs' title bars follow the theme in every theme.
- Text boxes apply their inner padding once, so text, caret and hints line up; the Browse / Load button sits inside the source box.
- On a small or high-DPI screen the window opens inside the work area (maximized when it nearly fills it), and an auto-hide taskbar wakes when the window is maximized.
- The Cut Segments pane and the Video Combinator are retired from the editor. Settings shows the right version.
- Lower idle memory, and a startup that no longer stalls while the graphics driver loads.

### Fixed
- **A paused seek or keyframe step drifted up to two seconds forward** a moment later, off the keyframe, greying the Easing box.
- Clip submenus (Fade In / Fade Out, Speed, Proxy) never opened; their ticks and icons now show.
- Hardware exports of 4:4:4 or odd-sized screen recordings showed coloured stripes.
- Hover-scrub frames slid to the right inside their tile.
- + New Project now resets everything (vertical video, crop, look, captions, voiceover, markers, solo).
- The Layout Selector opened in the wrong place at 150 % scaling; the Media pane's Project | Folder switch was cut off in narrow panes.
- A Chat card asked for a size the timeline is too long to reach without saying so; it now does.
- Layer, audio and media rows read as type names to screen readers.

### Compatibility
- Projects stay **format 4**. Projects from 4.1.x, 4.0 and 3.x open in 4.2.0, and 4.2.0's projects open in 4.1.1; a layer's Mask Offset is the one thing 4.1.1 leaves out (the mask shows un-nudged).

## HandPeg v4.1.1

### Highlights
- **Drop shadows in 3D.** A shadow now has a distance, an angle (where the light comes from), a softness that feathers its edge, and a colour, on the Drop Shadow card and in Layer Properties. Shadows from older projects look exactly as they did.
- **Edit layers on the video.** In Editor Mode, click a layer on the Preview Player to select it, then drag, resize, turn it or move its anchor right there (Edit on Video, in the Layout pane, the playback bar and Settings). Only the selected layer is outlined, and nothing shows while playing.
- **Keyframes, several at a time.** Ctrl-click, Shift-click or draw a box to select keyframes across rows; drag them together, set their easing at once, copy them to another clip, delete them in one step.
- **Paste effects your way.** Paste Effects replaces a clip's stack (hold Shift, or tick Apply Paste to All Clips on Track, for the whole track); Paste Selected Effects... adds just the cards you tick.
- **A calmer Reno.** Reno is now a relaxed mid-tone studio palette: pewter panels, warm stone inputs and buttons, taupe wells, a soft parchment selected row and a slate title bar, with no glare. Reno and Reno Dark outline every tab.

### New
- **Export sample rate and channels:** Auto (Match Source / 48 kHz), 48000 or 44100 Hz; Stereo (2.0), Mono (1.0) or Match Source. On the Export tab's Audio Export group and the Audio tab, saved with projects and presets, with defaults in Settings > Export & Hardware.
- **Project Manager:** a 16:9 picture of each project's video that scrubs under the pointer (Show video & hover previews), and **+ New Project**, which asks about unsaved changes first.
- **Media pane to timeline:** in Editor Mode, files dragged from the Media pane onto the player or the master timeline go onto the timeline at the playhead.
- The rotation handle stays reachable when a layer touches the frame's top edge, and shows the angle while it turns.
- **Project format 4** (`.hproj`): sample rate, channels and the new shadow settings.

### Changed
- Editor Mode starts without the bottom status bar (the title bar shows the status and progress) and with the Audio tab after Layers. Settings saved by 4.1.0 are brought over once; Status Bar stays in Views > Tabs and Panes.
- The Export tab always has the audio codec, bitrate, mix and normalize options.
- Changing the codec or bitrate for all tracks keeps the tracks you set yourself, and a dropped track stays dropped.
- Tabs show the move pointer only while held; the border above the master timeline resizes the rows around it.
- Keyframe diamonds stay readable on light themes.

### Fixed
- The Media pane showed no pictures on its tiles; posters now appear at once from the cache.
- The Sample Rate and Channels boxes showed raw values like `[0, Auto ...]` instead of their names.
- The layer rows' Size % label was cut off at the left.
- The paused picture could step forward on its own when a waveform finished or the layout or theme previews changed.
- The Add Effect flyout stayed open after a click elsewhere.

### Compatibility
- Projects from 4.1.0, 4.0 and 3.x open in 4.1.1. Projects saved by 4.1.1 are format 4: open them in 4.1.1 or later (4.1.0 offers to open them read-only).

## HandPeg v4.1.0

### Highlights
- **Sound and picture stay in step after every seek.** Live Preview now counts every sound in the mix on the sequence's own clock, cuts a layer's sound exactly where its picture is cut, and gives each proxy its own clock, so seeking anywhere in a long multi-video timeline (with or without proxies) plays in sync, within a frame. Proxies are now made at a constant frame rate, which fixes drift on recordings with variable or clustered time stamps.
- **Five themes with a look of their own.** Windows System, OLED Black (true black), Leonardo (a dark grading studio with one coral line tracing what is active), Reno (a warm pewter studio light with squared, embossed buttons and lit edges) and Reno Dark (gunmetal, embossed, steel blue). Pick one from the Themes row under the Layout button and see it at once. The active tab is traced in every theme.
- **The Layout button, turned around.** Resting on it opens the Layout Selector: a card per layout, the window taking each one as you point at it, with the layout and the theme in use marked. A click opens the menu (side window, built-in and saved layouts, Save Current Layout, Reset Layout).
- **One Export tab.** Video, Audio, Output and Chapters settings, with Save As, Start Encode and Add to Queue, in one tab (on by default in Editor Mode, where Video, Audio and Chapters start hidden). It is the last tab before Command Preview.
- **More room for the picture.** A slim 32 px title bar of HandPeg's own, where an export's progress is a dimmed fill across its full width with the status centred over it. The status bar can be hidden from its right-click menu. **Tabs and Panes** (the Views menu, or right-click any tab) is one list to show or hide every tab and pane.
- **Faster to launch.** About 0.5 s to the first frame in Release builds (0.6 s in 4.0), and the busy "working in background" pointer that spun for about 5 seconds after launch is gone.
- **Crash reports with no personal data.** If HandPeg falls over, it writes a small plain-text report (the error, the parts of HandPeg that were running, its last steps, the kind of media and of computer) with no file names, paths, user names, serial numbers or words from your media. A tiny watcher process catches crashes in libmpv and graphics drivers too. Reports stay on your PC and are never sent anywhere: attach one to a GitHub issue to share it. Settings > General > Diagnostics (and the new Privacy Policy) shows them, opens their folder, clears them, or switches them off.

### New
- **Pinned engines:** FFmpeg, libmpv and whisper.cpp install and update only to builds tested with this version of HandPeg (checked by SHA-256). Settings can allow newer untested builds behind a warning, and Tools & Backup has Restore Pinned Recommended Versions. yt-dlp still follows its newest release.
- **Project format 3** (`.hproj`), with the version of HandPeg that wrote it. Projects from HandPeg 1.x (`.txt`) are brought over; a project from a newer HandPeg is never read as if it were known (it offers to open it read-only, for Save As).
- **Portable copy:** the portable zip keeps all of its data beside `HandPeg.exe`; on its first start it copies your installed settings, presets, projects and styles.
- HandPeg is now under the MIT license (`LICENSE`).

### Changed
- A project or file opened in Editor Mode waits, paused, on its first frame instead of playing.
- Long filter graphs go to FFmpeg in a file, so very large timelines never run into Windows' command-line limit.
- Buttons, fields, lists and tabs follow the theme's look everywhere (corners, outlines, bevels, hover and selection colours).
- The mode switch is an icon button.

### Fixed
- Sound could play ahead of the picture, or drop out, after a seek in timelines with several videos.
- A layer's sound could start from the wrong moment of its file.
- Proxies of variable-frame-rate recordings drifted against the original over long files.
- The maximized window now fits the screen's work area exactly.

### Compatibility
- Projects from 4.0 and 3.x open in 4.1. Projects saved by 4.1 are format 3: open them in 4.1 or later.

## HandPeg v4.0.0

### Highlights
- **Live Preview that keeps up.** The player builds its preview for a stretch of time around the playhead and seeks inside each video file instead of decoding it from the start, so long timelines, and clips late in long recordings, play and scrub at once (seeking back from late in a long sequence went from about 14 s to about 1 s). Seeks land on the nearest I-frame, which HandPeg remembers for every file, in its cache and inside saved projects. A seek or an edit while paused shows the picture at once and gets the sound ready just after, so Play starts immediately. The player engine loads only when it is needed.
- **Proxies.** Heavy files (larger than 1080p, AV1, VP9, ProRes, 10-bit HEVC, or I-frames far apart) get a small editing copy made in the background while you are idle, on the second graphics card when there is one. Proxies are for the player only: exports always read the original files. An optional composed timeline proxy plays the whole sequence from one file. Per clip, right-click > Proxy chooses Automatic, Make Proxy or Use Original. Every setting, with the cache's size, usage, Clear and clean-up rules, is in Settings > Preview & Cache.
- **Effect stacks.** The new Effects pane shows the selected clip's effects as cards, in the order they are applied: add, reorder, switch off, and animate them, with a time range per card. Blend modes, adjustment layers, and a Sequence Output stack for the whole picture. Effects a newer version adds are kept, not lost, by this one.
- **Keyframe lanes and curves.** The Keyframes pane has a lane for each property (and each animated effect value), keyed, moved and deleted on its own, with a graph of the selected value over time. New easings: Hold, Back, Elastic, Bounce and Custom Bezier. Layers turn and scale about an anchor point that can be moved (a dot in the layout pane).
- **Fades and transitions on every clip.** Drag the handles at a clip's top corners to fade its picture and sound in or out, over whatever is under it; choose dissolve, wipes, slides, zooms or a blur dissolve. Crossfades from a cut's menu or by letting a clip overlap its neighbour, also between clips on different tracks. Works in the export and in Live Preview.
- **Timeline editing.** Ripple trim and ripple delete, roll, slip, trims at a clip's start, and auto-scroll while dragging; drag clips up or down onto another track; solo as a set (Alt-click for alone); gain lines; an eye button to hide a track. The layout pane has a rotation handle, the anchor dot, a motion path, snap guides and arrow-key nudges.
- **Layouts.** The new Layout button (in the header, and in Encoder Mode's top bar) offers three built-in layouts (Standard Editing, Color & Effects, Assembly & Media), three of your own per mode, Save Current Layout and Reset Layout; the Side Window switch is in its menu now. A click opens the Layout Selector: point at a layout and the window takes it, click to keep it, move away to put yours back. Standard Editing is where both modes start.
- **Docking.** A tab dragged over a row of tabs goes exactly where the accent line shows, so tabs can be reordered, and only the tab dragged moves. The header joins the row of boxed tabs under it, or stands as a strip of icons beside other panes. The video no longer blinks when panes are rearranged. Settings tabs rearrange themselves in a narrow pane and scroll instead of being cut off.
- **Background work you can see and steer.** A progress chip in the status bar lists everything running in the background, with progress and Cancel, and pauses background or idle work. Background tools run at low priority, off reserved cores, in Windows' efficiency mode, and wait while you play or export (Settings > Export & Hardware > Performance).

### New
- **Add Media** in Editor Mode's header (in place of the Source pane): a click picks files to open or add at the playhead; resting on it opens a box for a path or a link, with the download resolution.
- **Media pane:** a Project Media / Folder Browser switch; a folder left open is indexed in the background so every video is ready to scrub (not on battery or network folders unless allowed).
- **Add Layer** adds an empty track; dropped clips go onto a new track (or an empty one), never over another clip.
- **Updates:** FFmpeg, yt-dlp, whisper and libmpv update themselves in the background; HandPeg checks for its own update at startup and the status bar's Update button installs it.
- **Video layers while scrubbing** (Settings): live, a still frame, or hidden.
- **Downloads used by your projects are kept,** and saving a project offers to copy downloaded videos it uses into your download folder.
- **Cache clean-up at startup** for entries whose source file is gone (never for a drive that is only offline).
- The Summary tab's cards can be dragged into the order you like.

### Changed
- **Faster start:** the first frame is the finished window (no panes or tabs appearing after it), drawn about 0.6 s after launch; HandPeg idles under 200 MB, also with the Media pane open on a large folder. The Media pane makes only the tiles on screen, makes the others' pictures ahead when the pointer comes into it, and lets them go a minute after.
- The playback bar and the Keyframes pane's buttons stay on one line; what does not fit goes into a "more" menu.
- Encoder Mode shows none of the editing panes or tools.
- I-frame lists and waveforms are made several times faster (all of a file's tracks in one pass).
- The hardware encoder test is remembered until FFmpeg or a graphics driver changes (Settings: Test Hardware Encoders Again).
- Tabs outside the main tab bar use compact inline tabs; tabs are reachable from the keyboard and by screen readers.

### Fixed
- Seeking back from late in a long sequence could take over ten seconds.
- A layer cut from late in a long file was decoded from the file's start on export (3 s at 317 s into a file: 26.6 s, now 0.7 s).
- Dragging one tab out of a group of panes could leave the panes it left behind invisible.
- The panes' arrangement was sometimes not saved when the window was closed.
- Controls were cut off in narrow panes (the playback bar's right end, the Keyframes buttons, the settings tabs).

### Compatibility
- Projects from 3.x open in 4.0. Projects saved by 4.0 carry effect stacks, fades, transitions and seek facts that 3.x does not know; open them in 4.0.
- A layer's blur is now a box blur; on old projects that used one it looks very slightly different.

## HandPeg v3.2.0

### Highlights
- **Arrange the window your way.** Every part of the window is a pane you can move: the player, the playback controls, the master timeline, the layout pane, the header, Source, Media, Keyframes, Cut Segments, the preset bar and each of the settings tabs. Drag a pane by its title (or, for the player, the playback controls and the timeline, by the tab that appears when the pointer reaches its top-left corner): onto another pane to tab them together, to one side of it to put it beside, to the edge of the window, or into the row of settings tabs. A settings tab can be dragged out to become a pane of its own. Tabs take the place of the titles, so nothing gets taller. Each mode keeps its own arrangement; right-click any tab (or use the Views menu) for Reset Panes Layout.
- **Side window for a second monitor.** Drag a pane out of the window, or right-click its tab and choose Move to Side Window, and it goes into a second HandPeg window that holds any number of panes, arranged as freely as the main one. The player can go there too and keeps playing. The new Side Window button (left of Project, both now icons) opens and closes it; closing it puts its panes back, and opening it again brings them back where they were. Its place is remembered.
- **Drop links.** The drop hint now reads "Drag & Drop Files or Links": drag a video link from a browser (any site yt-dlp supports, not only YouTube) onto the window to load it, onto a preset target to load it with that preset, onto the Layers or Audio tab to download it and add it as a clip, or onto the Squisher.
- **Markers.** Press M (or the Add Marker button) to put a marker at the playhead. Markers show on the time ruler and the master timeline, clips snap to them, Shift+M and Ctrl+Shift+M go to the next and previous one, and right-clicking one renames or deletes it. **Generate Chapters at Markers** (Video tab) writes a chapter at each marker.
- **Choose the playback controls.** A gear on the playback bar lists what it can show, per mode: Mark In / Mark Out (the cut segment start and stop), Go to In / Out, Play Start to Stop, Loop, previous / next edit point, jump to start / end, Add Marker and previous / next marker, Export Frame (saves the frame as a picture), Safe Margins, Mute, and the ones it had already.
- **Default encoding settings** (Settings): the container, encoder preset, rate control and quality or bitrate, frame rate, size, 10-bit, audio merge and normalize, and hardware decoding a session starts with, with Use Current and Reset buttons. An option applies them again after a style preset; an encoding preset always wins.

### Changed
- The built-in Overwatch vertical preset is now the hand-made "Overwatch Vertical HUDv2" style, with its cleaner mask.
- Media browser: sort by name, date, size or type (either way), and a size slider from a list to large tiles.

### Fixed
- Dragging a file over the "Drag & Drop" hint did not show the drop targets, and they appeared with a delay the first time. They are now prepared in the background after launch and appear wherever a file is dragged.

## HandPeg v3.1.0 (not released on its own: it ships in 3.2.0)

### Highlights
- **A timeline you can scroll and zoom, with clips end to end.** The Layers and Audio tabs have a time ruler over their bars, zoom buttons and a zoom slider, Fit, and a scrollbar under the bars. The wheel over the ruler (or Ctrl + wheel over a bar) zooms about the pointer; Shift + wheel or a middle-button drag moves along; `=`, `-` and `\` zoom in, out and back to the whole sequence. The bars reach past the end of the sequence (that room is shaded), so a clip can be dropped or dragged after the last one, and the sequence grows to hold it. While a video plays zoomed in, the view turns the page with the playhead.
- **Videos one after another.** Drop a video in the room after the main video, on the main video's row, and it starts where the main video ends, sized and placed as the main video is, on a track of its own. Drop more videos on that track (or several at once) and they follow each other end to end. A track can now hold clips of different files. A video placed at a moment plays all of itself, past the old end if need be.
- **Snapping.** A dragged or dropped clip's edges catch on the other clips' edges, the end of the sequence and the playhead (the magnet button turns it off; hold Alt to place a clip freely). A file dragged over the bars shows where it will land.
- **Media browser pane** (Editor Mode, Views menu): a folder's videos, sounds and pictures beside the player, with a path box, Up, Browse and Refresh, and List, Small, Medium or Large views. Move the pointer across a video's tile and it shows the frame at that point, like a small timeline of its own. Drag files from it onto a layer track, after the main video, onto the Audio tab or onto the player; double-click to add a file at the playhead (or load it when nothing is loaded); right-click for Load as Main Video, Add to Timeline, Add Sound and Show in Explorer. Each video's frames (15, 480 pixels tall, both set in Settings) are made in the background, only for the tiles on screen, and kept in a cache of their own.
- **Live audio preview works with muted tracks.** Muting tracks down to one (or all of them) silenced the preview, Solo went silent after the mix had played, and with Live Preview off (always, in Encoder Mode) the player kept playing the first track even when it was muted. The preview now plays exactly the tracks that are not muted, whatever the number.

### New
- **Timeline preview cache** (Settings, Preview & Cache): the hover previews, the frames on the blocks, the waveforms and the I-frame list of every video loaded are kept on disk, so a video opened again shows them at once. Switch it off, set its size, see how much it holds, or clear it.
- **Media browser cache**, with its own switch, size and Clear, and the browser's frames per video and frame height.
- **Download folder** (Settings, Preview & Cache): videos loaded from a URL can be saved to a folder of your own, where HandPeg never deletes them. Loading the same URL again uses the file already there.

### Fixed
- In Editor Mode with the video encoder set to Copy, the whole Layers tab was switched off, so cut points could not be set. The cut tools ([ and ], Manual Cut Entry, Split, Trim, Delete, Remove Dead Air) now work while copying; Split and Trim act on the cut segments, and only adding layers needs the video encoded.
- Files dropped on a zoomed timeline landed at the wrong moment, and the time bars of different rows could be different widths (each row's controls set its own), so clips on different rows did not line up in time. Every bar of a tab now spans the same stretch of the screen, and the arrow that folds the layers' controls away folds all rows together.
- A sound dropped after the end of the sequence got no length.

### Compatibility
- Projects made by 3.1 with clips of different files on one track open in 3.0 with those clips' files missing (they show the track's first file). Projects from 3.0 open unchanged.

## HandPeg v3.0.0

**HandPeg 3.0 is a rebuild, not 2.6.3 with more added.** The 2.6 series grew heavy machinery around the editor: proxies of every video, a rendered timeline proxy that had to be made, cached, found again and cleaned up, and captions kept as timeline data, which left Editor Mode exports refusing to run when nothing had been transcribed. 3.0 starts again from the 2.5.0 code base. It brings over the parts of 2.6 that worked, and the improvements that were being made after 2.6.3 but never released. It leaves the proxy machinery out, and adds HUD masks, live audio and fast cut exports. The aim is a dependable editor for everyday cutting, captioning and vertical HUD clips.

Everything below is compared with **2.6.3**, the last release. The 2.6 code stays in the repository's history (tags `v2.6.0` to `v2.6.3`), and installed copies of 2.6.x update to 3.0 as usual.

### Highlights
- **Captions work as they did in 2.5 again.** The Generate Auto-Captions checkbox is back in Editor Mode and is the on/off switch: ticked, the export has captions; cleared, it has none, and nothing stops an export. **Generate Captions** still transcribes ahead of time into a subtitle track you can watch and edit; exports and queued jobs then draw that track exactly as it stands and never transcribe it again.
- **Cut exports from long recordings start at once.** An export with cut segments opens the video wound forward to each kept stretch instead of decoding everything before it. Three clips from the last minutes of a 4.5-hour recording used to wait about 20 minutes before the first frame; they now export in under 40 seconds. Stretches close together share one reading of the file; widely spaced ones (up to 24) each get their own.
- **HUD masks of the whole frame.** One black-and-white mask of the whole video frame, white over every HUD element, can serve every layer cut from the video: each layer takes its own part of it and stays a layer of its own. Redrawing a layer's area never shifts the mask.
- **Draw Custom Shape.** Trace round a HUD element on a still of the video, point by point; the lines join up, and the shapes become a mask for one layer or for all of them.
- **Mask options:** grow or shrink the mask, invert it, crop any edge of a layer (what is cropped is see-through, the rest stays where it is), and Soft Edges now softens a mask's own outline. A mask can be a PNG with transparency: what is painted shows, what is see-through is cut away.
- **Layers from a Mask:** a mask of the whole frame is taken apart into one layer per HUD element. Patches closer together than a distance you set are one element, so ability icons traced one by one make a single layer. The pieces are shown on a still of the video before they are added. **Add More Pieces of This Mask** (right-click a layer) adds the ones not cut out yet.
- **Opacity keyframes:** layers can fade in and out, keyed like position, size and rotation.
- **Built-in HUD presets for vertical video,** three or four layers each, one per section of the HUD. **Overwatch** uses a hand-made mask of its HUD: Killfeed, Health & Name (with the hero portrait), and Abilities & Ammo. **Apex Legends, Marvel Rivals and The Finals** are traced from the games, with their other pieces (a minimap, an objective) in the mask, ready for Add More Pieces of This Mask. Counter-Strike 2 is kept as it was.
- **Live audio preview:** every audio track of a video is heard while it plays, not only the first, mixed with the layers' sound, and as the export will have it: silenced stretches, slips, ducking under the voice tracks, each track's gain and filters, and the fades. Changing any of them is heard straight away.
- **Projects and style presets carry their masks**, so one file is all another computer needs.

### New since 2.6.3
- **Settings** reorganized into seven tabs with search, including an Auto-Captions tab: default language and caption style, beam size, best-of, temperature and fallback, and silence-detection threshold and timings.
- **About, Check for Updates and Update Dependencies** on the General tab of Settings, with a **Manage Dependencies** link to the full list (tool paths, update URLs, speech models) on the Tools & Backup tab.
- When something needs a tool that was never downloaded (Start Encode, Add to Queue, Generate Captions, Edit Captions, loading a web address), HandPeg offers to download exactly that.
- Queued jobs transcribe their captions when they run, not when they are added, so adding a job never waits for whisper. A job whose captions are a subtitle track is written at once, as the track stands.
- Undo covers subtitle edits (in the Subtitle Editor and elsewhere), Generate Captions, importing and removing subtitles, the caption style and caption box, moving and resizing layers in the layout pane, layer properties, Draw Target and applying a style preset.
- First run preselects Editor Mode and the Minimal set of downloads.

### Changed since 2.6.3
- The player shows the original files again, as in 2.5: there are no proxies to wait for.
- **Render Preview** is back in Editor Mode in place of Render Timeline Proxy: it encodes a few seconds at reduced size and plays them.
- Editor Mode exports draw captions only while Generate Auto-Captions is ticked (see Highlights); in 2.6 they were drawn whenever the subtitle track had words.
- The Overwatch HUD preset's layers are fitted to its new mask and have no extra pieces.

### Kept from 2.6
- The drag-and-drop overlay in place of the launch window: drag a file over an empty window and drop it on a style preset (Editor Mode), an encoding preset (Encoder Mode) or a Social Squisher preset. Settings orders both lists and sets how many are shown.
- The Generate Captions button, which asks before replacing a subtitle track.
- The quick start: tabs built when first opened, settings and presets read while the window starts, the encoder check after the window is drawn, and the 2.6.1 and 2.6.2 optimizations.
- Scrubbing with quick seeks while dragging and one exact seek on release; the player leaving dropped files to HandPeg.
- Startup Window Size, a project file pasted into the Source box opening the project, Encoder Mode without Live Preview, and the Social Squisher's status lines.

### Fixed
- Exports in Editor Mode failing with "press Generate Captions first" when captions were on but nothing had been transcribed.
- Dropping a video on a style preset in the drag-and-drop overlay could close HandPeg (the player crashed on a HUD mask's soft edge).
- The large-v3 and large-v3-turbo speech models showed a 0 MB download size.

### Removed
- Proxies of the main video and the video layers, and Preview Quality (High, Medium, Low).
- The Timeline Proxy, its auto-render, and Render Timeline Proxy.
- The proxy settings (Enable Editor Proxies, Proxy Cache Size Limit, Auto-Render Timeline Proxy, Clear Proxy Cache on Exit) and the proxy cache clean-up at startup.
- Recent Editor Files (Encoder Mode).

### Compatibility
- Projects from 2.5 and 2.6 open unchanged, with their subtitle track; the Generate Auto-Captions checkbox follows what was saved. Compatibility with older presets and projects is no longer promised where it would hold new features back.
- Projects saved by 3.0 open in 2.5 and 2.6, but without their masks, which those versions cannot unpack.
- Settings and presets carry over. The proxy settings are ignored. Proxies made by 2.6 are no longer used: the folder `%LOCALAPPDATA%\HandPeg-Data\cache\proxies` can be deleted.
- The built-in HUD style presets are written again at the first start, replacing earlier copies of the same names. Presets of your own are not touched.

### Upgrading
Installed copies update automatically.

## HandPeg v2.6.3

A hotfix for the proxy cache.

### Fixed
- **Timeline proxies were rendered again every time.** A timeline proxy was given a random name, so nothing could find it again, and the clean-up at startup removed every one of them. It is now named after its timeline (the project as saved, the preview quality, and the size and date of each file on it): opening the same project again finds it in the cache and plays it at once, with nothing rendered. The startup clean-up keeps them; only an edit to the timeline discards one.
- **Proxy work carried on after another video or project was opened.** Opening a video or a project now stops whatever was being made for the one before, and ends its FFmpeg process at once: the proxy of each video, and the timeline proxy. Nothing half-made is left in the cache.
- Changing the preview quality while a proxy was being generated could leave the new tier's proxies waiting and never made.
- A proxy that is already in the cache is never encoded again, even when it was queued before it appeared there.

### Changed
- Timeline proxies count towards the Proxy Cache Size Limit, and the oldest files go to make room for a new one.
- The log says when a proxy was found in the cache, when generation was stopped, and (once for each file opened) that Live Preview's filter graph is running in the player.

### Live Preview
- The report that Live Preview had stopped rendering after the launch optimizations was looked into and could not be reproduced: with a plain video and with a project, in the debug and the published build, the filter graph reaches the player and is drawn. No change was made to the player for it. If it still happens, HandPeg.log now shows whether the graph was running ("Live Preview: the filter graph is running in the player") or what the player said was wrong with it ("Player: ...").

### Compatibility
- Projects, presets and settings are unchanged. Timeline proxies made by earlier builds cannot be found again and are removed at the next start.

### Upgrading
Installed copies update automatically.

## HandPeg v2.6.1

An optimization patch: nothing looks or works differently.

### Highlights
- **Sub-second boot enhancements.** The settings and presets (JSON) are read off the UI thread while the interface starts, and the hardware encoder probe and the dependency update check begin only after the window has been drawn. Measured on a published build: the window is on screen in about 490-510 ms, down from about 560-590 ms.
- **Bare-metal efficiency.** `libmpv` is called through unmanaged function pointers, and its playback position updates no longer allocate. Subtitle parsing (`.srt`, `.vtt`) reads `Span<T>` in place, and waveform rendering borrows its buffers from `ArrayPool`, so the garbage collector has much less to do during playback, imports and waveform drawing.

### Changed
- Windows calls are generated at compile time (`LibraryImport`).
- Every brush made in code is frozen; the palette brushes are frozen where they are declared.
- Whisper's progress line is read with a source-generated regex.

### Fixed
- **First-run setup.** Hardware encoders (NVENC, QuickSync, AMF) could stay missing from the setup window's encoder list until HandPeg was restarted: after "Use an Existing FFmpeg...", and after an install pass that ended without reporting anything newly installed. The list is now filled as soon as an FFmpeg is there, however it got there.
- The voiceover's waveform was not drawn in the Master Mix View (Encoder Mode).

### Compatibility
- Projects, presets and settings are unchanged from 2.6.0.

### Upgrading
Installed copies update automatically.

## HandPeg v2.6.0

### Highlights
- **Proxies.** In Editor Mode the player shows light, silent copies of the main video and of every video layer, made in the background on the graphics card. Seeking and scrubbing a project with several videos is immediate. Exports always read the originals.
- **Preview Quality tiers.** High (the originals), Medium (720-line proxies) and Low (360-line proxies), in the Views menu.
- **Timeline Proxy.** The whole composed timeline rendered once into a single file that the player plays in place of composing live. It renders itself when the timeline is left alone, and is discarded the moment anything is edited.
- **Captions are part of the project.** Generate Captions transcribes once and puts the captions on the timeline. From then on they are what is shown, edited, saved and exported; nothing is transcribed again behind your back.
- **Starts straight into the workspace.** The startup dialog is gone. Drag a file over an empty window and drop targets appear: style or encoding presets, and the Social Squisher's.
- **Encoder Mode stands apart.** No Live Preview, no Layers tab, no caption preview; a Recent Editor Files list opens the editor's files there.
- **Sub-second start.** The window is on screen in well under a second. Only the Summary tab is built with the window; every other tab, the drag-and-drop targets and the video player come in after the first paint.

### New
- Drag-and-drop overlay over an empty window, with a larger row of style presets (Editor Mode) or encoding presets (Encoder Mode) and a smaller row of Social Squisher presets. Settings → Drag & Drop Overlay sets how many of each are shown and their order.
- Proxies for the main video and every video layer: 720 lines, capped at 4 Mbit/s, an I-frame every 15 frames, no audio. Encoded with NVENC, QuickSync or AMF where available, otherwise libx264. Progress shows on the status bar.
- Preview Quality in the Views menu. Low makes 360-line proxies capped at 2 Mbit/s.
- Render Timeline Proxy in the Views menu, sized and capped by the preview quality.
- Settings → Performance: Enable Editor Proxies, Proxy Cache Size Limit (MB, default 4096), Auto-Render Timeline Proxy with an idle time in seconds (default 60), Clear Proxy Cache on Exit.
- Generate Captions button (Editor Mode), with a confirmation before it replaces an existing subtitle track.
- Recent Editor Files on the Summary tab in Encoder Mode.
- Startup Window Size (Settings → Interface): Default Size, Remember Last Size, Launch Maximized.
- A project file (`.hproj`) pasted into the Source box opens the project.
- Proxies unused for a week, and leftovers of earlier sessions, are cleared from the cache at startup.
- Twenty-four more Social Squisher status lines.
- Lazy-loaded tabs: Video, Filters, Layers, Audio, Subtitles, Chapters and Command Preview are built when first opened, or one at a time while the window is idle, whichever comes first.
- The drag-and-drop targets are built while the window is idle, and the player is started only after the window has been drawn.
- The log records how long each launch took ("Startup: window drawn N ms after the process started").

### Changed
- Editor Mode has no "Generate Auto-Captions" checkbox and no "Preview Subtitles" toggle. Captions show whenever the subtitle track has words. Encoder Mode keeps the checkbox and still transcribes at export.
- An Editor Mode export draws only the subtitles on the timeline. With none there, the export has no captions.
- "Render Preview" is replaced by Render Timeline Proxy.
- Scrubbing sends fast seeks at most 30 times a second while dragging and one exact seek on release.
- The player no longer takes dropped files itself, so a drop anywhere on the window goes where HandPeg puts it.
- The Social Squisher opens directly at its compact size when started from the overlay, and its presets fill their row.
- The Settings flyout lists Autosave first, with its checkbox at the right; all rows are left-aligned.

### Fixed
- Dropping a file on a preset doing nothing (the embedded player was taking the drop).
- The player staying bright while the rest of the window dimmed.
- Seek lag on projects with several videos.
- Proxy files several times larger than their source.

### Compatibility
- 2.5.0 projects open unchanged. A project saved with Auto-Captions on but no subtitle track opens in Editor Mode without captions until Generate Captions is pressed.
- Projects saved in 2.6.0 open in 2.5.0; their subtitle track is kept, and Auto-Captions follows what was saved.
- Proxies made by earlier builds are not reused and are removed by the startup sweep after a week.

### Known limitations
- Subtitle edits are not on the undo stack.
- Medium and Low timeline proxies are encoded twice (render, then cap), which is fast but not free.
- The timeline proxy is discarded by any change to the project, including encoder settings that do not change the picture.
- There is still no published Whisper build for AMD or Intel graphics; transcription runs on the processor there.
- Image-based subtitles are preview-only.
- Switching modes keeps the same video open in both.

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
