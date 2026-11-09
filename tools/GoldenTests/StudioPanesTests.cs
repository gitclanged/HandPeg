using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.Docking;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using HandPegApp.Views;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.4C (Subagent C1): the Subtitle Studio, Voiceover Studio, Render Queue and Video Scopes as dock panes, and the new Vertical &amp;
/// Shorts Studio layout. dock-shorts-layout (window-free): the left group of the effects, keyframes, Canvas and (5.4D) Subtitle Studio as tabs,
/// the player beside it, the playback controls under both, the header joined over the media at the right, the scrubber, the Preset Bar and
/// the Timeline-first tab group; its Media Browser set to Filmstrip when taken up, kept or chosen at first run, never on a preview.
/// dock-optional-panes: a real dock manager (its window never shown) with the four studios registered as MainWindow registers them: no load
/// adds or makes one, Open puts each with its HomeWith mates, Close takes it out, a saved arrangement keeps it, the Shorts layout shows the
/// Subtitle Studio and Standard Editing closes it. studio-panes-source: no Subtitle Editor left, the old windows gone, the panes UserControls
/// made by factories (never in PaneLibrary), listed in Tabs and Panes, the Subtitle Studio never setting the modal flag. subtitle-studio-pane:
/// a real pane over a real view model in a window: each gesture one step of the main history, the main Undo and Redo, the playhead the
/// player's, Delete its own key, Space and Play the player's, listening only while loaded. video-scopes-math: the Histogram, the Luma
/// Waveform and the RGB Parade counted on synthetic frames (BT.709 luma) and drawn. video-scopes-pane: a real pane fed by a stand-in capture:
/// captured once per burst of changes, never while playing ("Updates when paused"), another scope drawn from the last frame.
/// </summary>
public static class StudioPanesTests
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // The settings tabs' pane ids after 5.4B, and the other panes MainWindow registers.
    private static readonly IReadOnlyList<string> TabIds = ["Tab:Filters", "Tab:Audio", "Tab:Export", "Tab:Command Preview"];
    private static readonly string[] PaneIds = ["Player", "Playback", "Timeline", "UnifiedTimeline", "Layout", "Header", "Media", "Keyframes", "Effects", "Meter", "Presets"];

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("dock-shorts-layout", "# The Vertical & Shorts Studio: Effects | Keyframes | Canvas | Subtitle Studio as tabs, the player, the playback controls under both, the media at the right, the Timeline-first tabs below.", ShortsLayout);
        yield return Case("dock-optional-panes", "# The studios as optional panes: never added by a load, opened with their mates, closed, kept in a saved arrangement, shown by the Shorts layout.", OptionalPanes);
        yield return Case("studio-panes-source", "# The studios are dock panes: UserControls made by factories, listed in Tabs and Panes, the old windows and the Subtitle Editor gone.", SourceChecks);
        yield return Case("subtitle-studio-pane", "# The Subtitle Studio pane over a real view model: each gesture one step of the main history, the player's playhead and Play, Delete its own.", SubtitleStudio);
        yield return Case("video-scopes-math", "# The Video Scopes' maths: the Histogram, the Luma Waveform and the RGB Parade counted on synthetic frames with BT.709 luma, and drawn.", ScopesMath);
        yield return Case("video-scopes-pane", "# The Video Scopes pane: one capture for a burst of changes, none while playing (Updates when paused), another scope drawn from the last frame.", ScopesPane);
    }

    private static GraphResult Case(string name, string title, Action<Action<bool, string>> run)
    {
        var text = new StringBuilder(title).Append('\n');
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            run(Expect);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult(name, text.ToString(), problems, 0);
    }

    // Pass 5.4D (Q6 a): the dead modal flag may be gone from the view model; "off" then holds by construction.
    private static bool ModalFlagOff(MainViewModel vm) =>
        typeof(MainViewModel).GetProperty("IsSubtitleEditorOpen") is not { } flag || flag.GetValue(vm) is false;

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("HandPegApp.csproj was not found above the golden tests' program.");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepositoryRoot(), "src", .. parts]));

    /// <summary>A method's text in a source file: from its signature to the first line that closes it at its own indent.</summary>
    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            return "";
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    /// <summary>Runs the dispatcher for a while: its timers, its queued work and the windows' layout.</summary>
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static string Shape(DockNode node) => DockManager.Shape(node);

    // ----- dock-shorts-layout -----

    private static void ShortsLayout(Action<bool, string> expect)
    {
        var shorts = MainWindow.ShortsLayout(TabIds).Main;
        const string shape = "V(H(V(H([Effects,Keyframes,Layout,SubtitleStudio];[Player]);[Playback]);V([Header];[Media]));[Timeline];[Presets];"
                             + "[Tab:Audio,Tab:Command Preview,Tab:Export,Tab:Filters,UnifiedTimeline])";
        expect(Shape(shorts) == shape, "its shape: " + Shape(shorts));

        // 5.4D: the Subtitle Studio is a tab of the left group, after the effects, keyframes and Canvas (it was docked under them).
        var top = shorts.Children[0];
        var body = top.Children[0];
        var stage = body.Children[0];
        var left = stage.Children[0];
        expect(left is { IsGroup: true } && left.Panes.SequenceEqual(["Effects", "Keyframes", "Layout", MainWindow.SubtitleStudioId]) && left.Active == "Effects",
            "the left group: Effects | Keyframes | Canvas | Subtitle Studio as tabs (in that order), the effects shown");
        expect(stage is { Vertical: false } && stage.Sizes.SequenceEqual([40.0, 60]) && stage.Children[1].Panes.SequenceEqual(["Player"]),
            "the player beside the left group (40 to 60: a 9:16 frame needs little width)");
        expect(body is { Vertical: true } && body.Children[1].Panes.SequenceEqual(["Playback"]),
            "the playback controls are the row under the left column and the player together (V(H(left, Player), Playback))");
        var right = top.Children[1];
        expect(top is { Vertical: false } && top.Sizes.SequenceEqual([70.0, 30]) && right is { Vertical: true } && right.Children[0].Panes.SequenceEqual(["Header"])
               && right.Children[1].Panes.SequenceEqual(["Media"]),
            "the right column: the header joined over the media, 30 of the width");
        var bottom = shorts.Children[3];
        expect(shorts.Children[1].Panes.SequenceEqual(["Timeline"]) && shorts.Children[2].Panes.SequenceEqual(["Presets"]) && bottom.Panes[0] == "UnifiedTimeline"
               && bottom.Active == "UnifiedTimeline" && TabIds.All(bottom.Panes.Contains) && bottom.Panes.Count == TabIds.Count + 1,
            "under it the Sequence Overview Scrubber across the window, the Preset Bar, then the timeline first and shown among every settings tab");
        expect(!shorts.AllPanes().Contains("Meter") && !shorts.AllPanes().Any(p => p is MainWindow.VoiceoverStudioId or MainWindow.RenderQueueId or MainWindow.VideoScopesId),
            "the Master Meter and the other studios are left out (the meter goes in with the effects when shown)");
        expect(Shape(DockTree.Tidy(MainWindow.ShortsLayout(TabIds).Main)!) == shape, "tidying leaves it as it is (the playback row under the inner split survives)");
        expect(MainWindow.BuiltInLayoutStates(TabIds).Count(l => l.Name == "Vertical & Shorts Studio") == 1 && MainWindow.BuiltInLayoutStates(TabIds).Count == 5,
            "it is still named Vertical & Shorts Studio among the five built-in layouts (5.4D: the Vertical Creator Split the fifth)");

        var standard = MainWindow.StandardLayout(TabIds).Main.AllPanes().ToList();
        expect(DockLayoutState.CurrentVersion == 4 && !standard.Any(p => MainWindow.Studios.Any(s => s.Id == p)),
            "Standard Editing holds no studio and the arrangement version is still 4");

        // What it sets besides the arrangement: the Media Browser's Filmstrip view, when taken up, kept or chosen at first run, never while previewed.
        var layouts = Read("MainWindow.Layouts.cs");
        expect(Regex.IsMatch(layouts, @"new\(""Vertical & Shorts Studio"", ""[^""]*Subtitle Studio[^""]*"", ShortsLayout\(TabIds\), null, ShowShortsMedia\)")
               && Method(layouts, "private void ShowShortsMedia()").Contains("MediaBrowser.ViewMode = Views.MediaBrowserView.Filmstrip", StringComparison.Ordinal),
            "its choice describes the Subtitle Studio and sets the Media Browser to Filmstrip (ShowShortsMedia)");
        // 5.4D: the Layout menu (and its TakeUpLayout) is retired; Then runs where a layout is put back by Reset Layout or opened in at start-up.
        expect(Method(layouts, "private void ResetToDefaultLayout(").Contains("choice?.Then?.Invoke();", StringComparison.Ordinal)
               && Method(layouts, "private void KeepLayout(").Contains("choice.Then?.Invoke();", StringComparison.Ordinal)
               && Method(layouts, "private string? TakeFirstRunLayout()").Contains("chosen?.Then?.Invoke();", StringComparison.Ordinal)
               && Method(layouts, "private string? StartupLayoutJson()").Contains("chosen.Then?.Invoke();", StringComparison.Ordinal),
            "Then runs when a layout is kept from the Layout Selector, put back as the Default by Reset Layout, chosen in the first-run window, and opened in at start-up");
        expect(!Method(layouts, "private void PreviewLayout(").Contains("Then", StringComparison.Ordinal) && !Method(layouts, "private void RestoreLayoutBeforePreview()").Contains("Then", StringComparison.Ordinal),
            "never while a card is only previewed, nor when the preview is undone");

        var firstRun = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Dialogs", "FirstRunWindow.xaml"));
        var card = firstRun.Descendants().FirstOrDefault(e => e.Attribute(X + "Name")?.Value == "ShortsLayoutCard");
        expect(card?.Attribute("Tag")?.Value == "Vertical & Shorts Studio" && card.Descendants().Any(e => e.Attribute("Text")?.Value.Contains("Subtitle Studio as tabs", StringComparison.Ordinal) == true),
            "the first-run card keeps its name (its Tag) and tells of the Subtitle Studio as a tab");
    }

    // ----- dock-optional-panes -----

    private static DockManager NewDock(Dictionary<string, int> made)
    {
        var dock = new DockManager(new Window(), new ContentControl(), () => MainWindow.StandardLayout(TabIds));
        foreach (var id in PaneIds.Concat(TabIds))
        {
            var content = new Border { Child = new TextBlock { Text = id } };
            dock.Register(new DockPane
            {
                Id = id, Title = id, Content = () => content, Handle = id.StartsWith("Tab:", StringComparison.Ordinal) ? PaneHandle.Tab : PaneHandle.TitleSlot,
                MinWidth = () => 160, MinHeight = 60,
            });
        }

        // The studios as MainWindow registers them (MainWindow.StudioPane), each factory counting what it makes.
        foreach (var (id, _, _, _) in MainWindow.Studios)
        {
            made[id] = 0;
            dock.Register(MainWindow.StudioPane(id, () =>
            {
                made[id]++;
                return new Border();
            }, () => dock.Close(id), 200, 140));
        }

        return dock;
    }

    private static DockLayoutState MainOf(DockManager dock) => DockLayoutState.Read(dock.Save())!;

    private static void OptionalPanes(Action<bool, string> expect)
    {
        expect(MainWindow.Studios.Select(s => $"{s.Id}={s.Title}").SequenceEqual(["SubtitleStudio=Subtitle Studio", "VoiceoverStudio=Voiceover Studio", "RenderQueue=Render Queue", "VideoScopes=Video Scopes"]),
            "four studios, their ids and titles: " + string.Join(", ", MainWindow.Studios.Select(s => $"{s.Id}={s.Title}")));
        var made = new Dictionary<string, int>();
        var dock = NewDock(made);
        var pane = dock.Pane(MainWindow.SubtitleStudioId);
        expect(pane is { Optional: true, Handle: PaneHandle.TitleSlot } && pane.IsShown() && pane.HomeWith.SequenceEqual(["Effects", "Keyframes", "Layout"])
               && dock.Pane(MainWindow.VideoScopesId)?.HomeWith.SequenceEqual(["Meter", "Effects"]) == true
               && dock.Pane(MainWindow.RenderQueueId)?.HomeWith.SequenceEqual(["Tab:Export", "UnifiedTimeline"]) == true
               && dock.Pane(MainWindow.VoiceoverStudioId)?.HomeWith.SequenceEqual(["Tab:Audio", "UnifiedTimeline"]) == true,
            "each is optional, always shown while held, with its title in its own row; homed with Effects/Keyframes/Canvas, Meter/Effects, Export/Timeline and Audio/Timeline");

        dock.Load(null);
        expect(MainWindow.Studios.All(s => !dock.Contains(s.Id)) && made.Values.All(n => n == 0),
            "a load of Standard Editing adds none of them and makes none of them (Law 1: nothing is built for a studio not shown)");

        foreach (var (id, title, _, _) in MainWindow.Studios)
        {
            dock.Open(id);
            dock.Open(id);
            var group = MainOf(dock).Main.GroupOf(id);
            var count = MainOf(dock).Main.AllPanes().Count(p => p == id);
            var mates = id switch
            {
                MainWindow.SubtitleStudioId => new[] { "Effects", "Keyframes", "Layout" },
                MainWindow.VideoScopesId => ["Meter"],
                MainWindow.RenderQueueId => ["Tab:Export", "UnifiedTimeline"],
                _ => ["Tab:Audio", "UnifiedTimeline"],
            };
            expect(group is not null && mates.All(group.Panes.Contains) && count == 1,
                $"Open {title} (twice): once, a tab beside {string.Join(", ", mates)}");
        }

        Pump(60);
        foreach (var (id, title, _, _) in MainWindow.Studios)
        {
            dock.Close(id);
            expect(!dock.Contains(id), $"Close takes {title} out again");
        }

        // A saved arrangement that holds one keeps it where it was; taking up a layout without it closes it.
        dock.Open(MainWindow.RenderQueueId);
        var saved = dock.Save();
        dock.Load(null);
        expect(!dock.Contains(MainWindow.RenderQueueId), "the defaults (Reset) leave it out");
        dock.Load(saved);
        expect(dock.Contains(MainWindow.RenderQueueId) && MainOf(dock).Main.GroupOf(MainWindow.RenderQueueId)?.Panes.Contains("Tab:Export") == true
               && MainWindow.Studios.Count(s => dock.Contains(s.Id)) == 1,
            "a saved arrangement holding the Render Queue loads with it where it was, and with no other studio");

        var before = made[MainWindow.SubtitleStudioId];
        dock.Apply(dock.Prepared(MainWindow.ShortsLayout(TabIds)));
        var shorts = MainOf(dock).Main;
        // 5.4D: the Subtitle Studio is a tab behind the effects: made only once it is shown (RenderTabs makes the tab shown and those made before).
        expect(dock.Contains(MainWindow.SubtitleStudioId) && shorts.GroupOf(MainWindow.SubtitleStudioId)?.Panes.SequenceEqual(["Effects", "Keyframes", "Layout", "Meter", MainWindow.SubtitleStudioId]) == true
               && !dock.Contains(MainWindow.RenderQueueId) && made[MainWindow.SubtitleStudioId] == before,
            "taking up the Shorts layout holds the Subtitle Studio as a tab behind the effects (not made while behind them) and closes the Render Queue it leaves out");
        dock.BringForward(MainWindow.SubtitleStudioId);
        Pump(60);
        expect(made[MainWindow.SubtitleStudioId] == Math.Max(before, 1) && MainOf(dock).Main.GroupOf(MainWindow.SubtitleStudioId)?.Active == MainWindow.SubtitleStudioId,
            "brought to the front of its group, it is shown (made once, when first shown)");
        expect(shorts.GroupOf("Meter") is { } studioGroup && studioGroup.Panes.Contains("Effects") && studioGroup.Panes.Contains(MainWindow.SubtitleStudioId)
               && shorts.ParentOf(studioGroup) is { Vertical: false } stage && stage.Children[1].Panes.SequenceEqual(["Player"]),
            "the dock puts the Master Meter in with the effects (its Standard Editing mates), the studio among them, the group beside the player");
        expect(shorts.GroupOf("UnifiedTimeline") is { Active: "UnifiedTimeline" } bottom && bottom.Panes[0] == "UnifiedTimeline", "the timeline is first and shown in the bottom group");
        dock.Apply(dock.Prepared(MainWindow.StandardLayout(TabIds)));
        expect(!dock.Contains(MainWindow.SubtitleStudioId), "taking up Standard Editing closes it again");
    }

    // ----- studio-panes-source -----

    private static void SourceChecks(Action<bool, string> expect)
    {
        var root = RepositoryRoot();
        var src = Path.Combine(root, "src");
        var withEditor = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("Subtitle Editor", StringComparison.Ordinal)).Select(f => Path.GetRelativePath(src, f)).ToList();
        expect(withEditor.Count == 0, "no \"Subtitle Editor\" is left in src (it is the Subtitle Studio)" + (withEditor.Count > 0 ? ": " + string.Join(", ", withEditor) : ""));

        string[] gone = ["Views/SubtitleEditorWindow.xaml", "Views/SubtitleEditorWindow.xaml.cs", "Dialogs/VoiceoverStudioDialog.xaml", "Dialogs/VoiceoverStudioDialog.xaml.cs", "Dialogs/QueueWindow.xaml", "Dialogs/QueueWindow.xaml.cs"];
        expect(gone.All(f => !File.Exists(Path.Combine(src, f))), "the old windows' files are gone: " + string.Join(", ", gone.Select(f => Path.GetFileName(f).Split('.')[0]).Distinct()));

        var panes = new[] { typeof(SubtitleStudioPane), typeof(VoiceoverStudioPane), typeof(RenderQueuePane), typeof(VideoScopesPane) };
        expect(panes.All(t => t.IsSubclassOf(typeof(UserControl)) && !t.IsSubclassOf(typeof(Window))), "the four are UserControls, not windows: " + string.Join(", ", panes.Select(t => t.Name)));
        foreach (var (file, id) in new[] { ("SubtitleStudioPane.xaml", "SubtitleStudio"), ("VoiceoverStudioPane.xaml", "VoiceoverStudio"), ("RenderQueuePane.xaml", "RenderQueue") })
        {
            var xaml = XDocument.Load(Path.Combine(src, "Views", file));
            expect(xaml.Root?.Name.LocalName == "UserControl" && xaml.Descendants().Any(e => e.Name.LocalName == "PaneTitleSlot" && e.Attribute("PaneId")?.Value == id),
                $"{file}: a UserControl with its title slot for {id}");
        }

        var scopes = Read("Views", "VideoScopesPane.cs");
        expect(scopes.Contains("new PaneTitleSlot { PaneId = MainWindow.VideoScopesId }", StringComparison.Ordinal), "VideoScopesPane (built in code): its title slot for VideoScopes");
        expect(typeof(IOwnsKeys).IsAssignableFrom(typeof(SubtitleStudioPane)), "the Subtitle Studio claims its keys (IOwnsKeys)");

        // Never in PaneLibrary (parsed before the first frame whatever the arrangement says), made by factories, never realized when idle.
        var main = Read("MainWindow.xaml");
        expect(panes.All(t => !main.Contains(t.Name, StringComparison.Ordinal)), "none of them is in MainWindow.xaml (its PaneLibrary is parsed before the first frame: Law 1)");
        var studios = Read("MainWindow.Studios.cs");
        var register = Method(studios, "private void RegisterStudioPanes()");
        expect(Regex.Matches(register, @"_dock\.Register\(StudioPane\((SubtitleStudioId|VoiceoverStudioId|RenderQueueId|VideoScopesId), \(\) => ").Count == 4,
            "RegisterStudioPanes registers the four with factories (made when first shown)");
        var docking = Read("MainWindow.Docking.cs");
        var realize = Method(docking, "private void RealizeTabsWhenIdle(");
        expect(realize.Length > 0 && !MainWindow.Studios.Any(s => realize.Contains(s.Id, StringComparison.Ordinal) || realize.Contains("Studio", StringComparison.Ordinal)),
            "none is made ahead at idle (RealizeTabsWhenIdle: Law 2)");
        expect(Regex.IsMatch(Read("MainWindow.Export.cs"), @"\.\. StudioToggles\(\),\s*\];") && Method(studios, "private IEnumerable<ViewToggle> StudioToggles()").Contains("_dock.Contains(s.Id)", StringComparison.Ordinal),
            "Tabs and Panes lists the four (PaneToggles ends with StudioToggles), each ticked while the arrangement holds it");

        // The openers open the panes; the Subtitle Studio is not modal and leaves the modal flag (and with it the captions and the main Undo) alone.
        expect(Method(studios, "private void OpenRenderQueue()").Contains("OpenStudio(RenderQueueId)", StringComparison.Ordinal)
               && Method(studios, "private void OpenVoiceoverStudio()").Contains("OpenStudio(VoiceoverStudioId)", StringComparison.Ordinal),
            "the Render Queue and Voiceover Studio openers open their panes");
        var captions = Read("MainWindow.Captions.cs");
        expect(Method(captions, "private async Task OpenSubtitleStudioAsync()").Contains("ConvertCaptionsToCuesAsync", StringComparison.Ordinal)
               && Method(captions, "private async Task OpenSubtitleStudioAsync()").Contains("OpenStudio(SubtitleStudioId)", StringComparison.Ordinal)
               && captions.Contains("internal void EditCaptions() => _ = OpenSubtitleStudioAsync();", StringComparison.Ordinal),
            "Edit Captions opens the Subtitle Studio pane, making the cues from the captions first when there are none");
        var studioCode = Read("Views", "SubtitleStudioPane.xaml.cs");
        expect(!captions.Contains("IsSubtitleEditorOpen", StringComparison.Ordinal) && !studioCode.Contains("IsSubtitleEditorOpen =", StringComparison.Ordinal)
               && !studioCode.Contains("ShowDialog", StringComparison.Ordinal) && !studioCode.Contains("CompositionTarget.Rendering", StringComparison.Ordinal)
               && !studioCode.Contains("RestoreSubtitleCues", StringComparison.Ordinal),
            "the Subtitle Studio is not modal, never sets IsSubtitleEditorOpen, runs no clock and keeps no undo of its own");
        expect(Method(studios, "private void CloseStudio(").Contains("FinishRecording()", StringComparison.Ordinal),
            "closing the Voiceover Studio finishes a recording under way, as closing its window did");
    }

    // ----- subtitle-studio-pane -----

    private sealed class StudioHost(MainViewModel vm) : ISubtitleStudioHost
    {
        public int Plays, Undos, Redos, Commits;

        public void TogglePlay() => Plays++;

        public void Undo(bool redo)
        {
            if (redo)
            {
                Redos++;
                vm.Redo();
            }
            else
            {
                Undos++;
                vm.Undo();
            }
        }

        public void SubtitlesCommitted() => Commits++;
    }

    private static List<(string What, byte[] State)> UndoSteps(MainViewModel vm) =>
        (List<(string What, byte[] State)>)typeof(MainViewModel).GetField("_undo", Hidden)!.GetValue(vm)!;

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    private static void SubtitleStudio(Action<bool, string> expect)
    {
        var vm = new MainViewModel();
        var host = new StudioHost(vm);
        var pane = new SubtitleStudioPane(vm, host);
        var window = new Window { Left = -6000, Top = -6000, Width = 720, Height = 420, Content = pane, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show();
            Pump(80);
            expect(pane.SummaryText.Text == "0 subtitles" && pane.AddButton.IsEnabled && !pane.DeleteButton.IsEnabled && !pane.CueText.IsEnabled && pane.OffsetBox.Text == "0",
                "loaded over an empty track: 0 subtitles, Add Caption on, nothing selected to edit, the offset 0");

            // Add Caption: a step of its own, the cue selected, the modal flag never set.
            var steps = UndoSteps(vm).Count;
            Click(pane.AddButton);
            var cue = vm.SubtitleCues.SingleOrDefault();
            expect(cue is not null && ReferenceEquals(pane.Timeline.Selected, cue) && pane.CueText.IsEnabled && UndoSteps(vm).Count == steps + 1 && UndoSteps(vm)[^1].What == "add a subtitle"
                   && ModalFlagOff(vm) && host.Commits >= 1,
                "Add Caption: one cue, selected, one step (\"add a subtitle\") of the main history; IsSubtitleEditorOpen stays off");

            // Typing: in the cue at once, one step once it rests.
            steps = UndoSteps(vm).Count;
            pane.CueText.Text = "hello there";
            expect(cue?.Text == "hello there" && UndoSteps(vm).Count == steps, "typing changes the cue at once, and takes no step while it goes on");
            Pump(SubtitleStudioPane.CommitAfterMs + 250);
            expect(UndoSteps(vm).Count == steps + 1 && UndoSteps(vm)[^1].What == "edit the subtitles", "once the typing rests it is one step (\"edit the subtitles\")");

            // The Undo and Redo buttons are the main window's.
            Click(pane.UndoButton);
            Pump(40);
            expect(host.Undos == 1 && vm.SubtitleCues.Count == 1 && vm.SubtitleCues[0].Text == "" && pane.CueText.Text == "", "Undo is the main Undo: the words typed are taken back, and the box shows it");
            Click(pane.RedoButton);
            Pump(40);
            expect(host.Redos == 1 && vm.SubtitleCues.Count == 1 && vm.SubtitleCues[0].Text == "hello there", "Redo is the main Redo: they come back");

            // A time typed in, and the offset as it is typed: one step each.
            pane.Timeline.Select(vm.SubtitleCues[0]);
            steps = UndoSteps(vm).Count;
            pane.StartBox.Text = "1.5";
            pane.StartBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, pane.StartBox));
            expect(vm.SubtitleCues[0].Start == 1.5 && UndoSteps(vm).Count == steps + 1, "a start typed in moves the cue: one step");
            pane.OffsetBox.Text = "250";
            expect(vm.SubtitleOffsetMs == 250 && Math.Abs(pane.Timeline.OffsetSeconds - 0.25) < 1e-9, "the Global Offset moves every cue as it is typed");
            pane.OffsetBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, pane.OffsetBox));
            expect(UndoSteps(vm).Count == steps + 2, "and is one step when the box is left");

            // The playhead is the player's.
            vm.PositionMs = 2000;
            expect(Math.Abs(pane.Timeline.PositionSeconds - 2) < 1e-9 && pane.TimeText.Text == "0:02.000", "the strip's playhead follows the player's (PositionMs 2000: 0:02.000)");

            // Split at the playhead (inside the cue, after the offset): a step of its own.
            pane.Timeline.Select(vm.SubtitleCues[0]);
            steps = UndoSteps(vm).Count;
            Click(pane.SplitButton);
            expect(vm.SubtitleCues.Count == 2 && vm.SubtitleCues[0].Text == "hello" && vm.SubtitleCues[1].Text == "there" && UndoSteps(vm).Count == steps + 1
                   && UndoSteps(vm)[^1].What == "split a subtitle" && ReferenceEquals(pane.Timeline.Selected, vm.SubtitleCues[1]),
                "Split at the playhead: \"hello\" | \"there\", one step, the second half selected");

            // Delete is the pane's own key while the keyboard is in it (not Ctrl+Delete, not Space: those stay the window's). The keyboard
            // is put on the pane as a press on its strip puts it (an undo now keeps the cue selected, its text box with it: 5.4C review).
            Keyboard.Focus(pane);
            expect(pane.OwnsKey(Key.Delete, ModifierKeys.None) && pane.OwnsKey(Key.Back, ModifierKeys.None) && !pane.OwnsKey(Key.Delete, ModifierKeys.Control) && !pane.OwnsKey(Key.Space, ModifierKeys.None),
                "it claims Delete and Back, not Ctrl+Delete, not Space");
            steps = UndoSteps(vm).Count;
            if (PresentationSource.FromVisual(pane) is { } source)
                pane.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Delete) { RoutedEvent = Keyboard.KeyDownEvent });
            expect(vm.SubtitleCues.Count == 1 && vm.SubtitleCues[0].Text == "hello" && UndoSteps(vm).Count == steps + 1 && UndoSteps(vm)[^1].What == "delete a subtitle" && pane.Timeline.Selected is null,
                "Delete in the pane deletes the selected cue: one step (\"delete a subtitle\")");

            // Play is the player's.
            Click(pane.PlayButton);
            vm.IsPlaying = true;
            var playing = pane.PlayButton.Content as string;
            vm.IsPlaying = false;
            expect(host.Plays == 1 && playing == "" && pane.PlayButton.Content as string == "", "Play plays the main player, and its glyph follows the player's state");

            // Out of the window it listens to nothing; back in, it shows the playhead as it now is.
            window.Content = null;
            Pump(40);
            vm.PositionMs = 3000;
            var away = pane.TimeText.Text;
            window.Content = pane;
            Pump(80);
            expect(away == "0:02.000" && pane.TimeText.Text == "0:03.000", "taken out (a dock rebuild) it stops following; put back, it follows again");
            expect(ModalFlagOff(vm), "IsSubtitleEditorOpen was never set: the main player kept drawing the captions throughout");
        }
        finally
        {
            window.Close();
        }
    }

    // ----- video-scopes-math -----

    private static byte[] Frame(int width, int height, Func<int, int, (byte R, byte G, byte B)> colour)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = colour(x, y);
                var at = (y * width + x) * 4;
                (bgra[at], bgra[at + 1], bgra[at + 2], bgra[at + 3]) = (b, g, r, 255);
            }
        }

        return bgra;
    }

    private static (int R, int G, int B) Rgb(uint pixel) => ((int)(pixel >> 16) & 0xFF, (int)(pixel >> 8) & 0xFF, (int)pixel & 0xFF);

    private static void ScopesMath(Action<bool, string> expect)
    {
        expect(Math.Abs(VideoScopes.LumaRed + VideoScopes.LumaGreen + VideoScopes.LumaBlue - 1) < 1e-12 && VideoScopes.LumaRed == 0.2126 && VideoScopes.LumaGreen == 0.7152 && VideoScopes.LumaBlue == 0.0722,
            "BT.709's luma weights: 0.2126, 0.7152, 0.0722");
        var lumas = new[] { VideoScopes.Luma(255, 255, 255), VideoScopes.Luma(0, 0, 0), VideoScopes.Luma(255, 0, 0), VideoScopes.Luma(0, 255, 0), VideoScopes.Luma(0, 0, 255), VideoScopes.Luma(128, 128, 128) };
        expect(lumas.SequenceEqual([255, 0, 54, 182, 18, 128]), "luma of white, black, red, green, blue, mid grey: " + string.Join(", ", lumas) + " (rounded, white stays 255)");
        var roundsLikeDoubles = true;
        for (var v = 0; v < 256; v += 5)
            roundsLikeDoubles &= Math.Abs(VideoScopes.Luma(v, 255 - v, v / 2) - (0.2126 * v + 0.7152 * (255 - v) + 0.0722 * (v / 2))) <= 0.51;
        expect(roundsLikeDoubles, "the whole-number luma is the weighted sum rounded, across a sweep of colours");

        // A grey ramp: one column per level, four rows.
        var ramp = Frame(256, 4, (x, _) => ((byte)x, (byte)x, (byte)x));
        var histogram = VideoScopes.Histogram(ramp, 256, 4);
        expect(Enumerable.Range(0, 256).All(l => histogram[0, l] == 4 && histogram[1, l] == 4 && histogram[2, l] == 4 && histogram[3, l] == 4),
            "the Histogram of a 256 x 4 grey ramp: four pixels at every level of red, green, blue and luma");
        var waveform = VideoScopes.LumaWaveform(ramp, 256, 4, 256);
        expect(Enumerable.Range(0, 256).All(c => waveform[c, c] == 4 && Enumerable.Range(0, 256).Sum(l => waveform[c, l]) == 4),
            "its Luma Waveform at 256 columns: column x holds its four pixels at level x");
        var halved = VideoScopes.LumaWaveform(ramp, 256, 4, 128);
        expect(Enumerable.Range(0, 128).All(c => halved[c, 2 * c] == 4 && halved[c, 2 * c + 1] == 4 && Enumerable.Range(0, 256).Sum(l => halved[c, l]) == 8),
            "at 128 columns each column takes two of the frame's: eight pixels at levels 2c and 2c+1");

        // A flat red frame.
        var red = Frame(10, 10, (_, _) => (255, 0, 0));
        var redLevels = VideoScopes.Histogram(red, 10, 10);
        expect(redLevels[0, 255] == 100 && redLevels[1, 0] == 100 && redLevels[2, 0] == 100 && redLevels[3, 54] == 100,
            "a flat red 10 x 10: red all at 255, green and blue at 0, the luma at 54");
        var parade = VideoScopes.RgbParade(red, 10, 10, 5);
        expect(Enumerable.Range(0, 5).All(c => parade[0, c, 255] == 20 && parade[1, c, 0] == 20 && parade[2, c, 0] == 20),
            "its RGB Parade at 5 columns: 20 pixels a column, red at 255, green and blue at 0");
        expect(VideoScopes.Histogram(red.AsSpan(0, 8), 10, 10)[0, 255] == 2 && VideoScopes.LumaWaveform([], 10, 10, 4).Length == 4 * 256,
            "a frame shorter than its size says counts what there is, and an empty one nothing");

        // Drawn: one row per level, 255 at the top, on a dark ground with a graticule every quarter.
        expect(VideoScopes.RowOf(255) == 0 && VideoScopes.RowOf(0) == 255 && VideoScopes.Brightness(0, 100) == 0 && VideoScopes.Brightness(100, 100) == 255
               && VideoScopes.Brightness(1, 100) < VideoScopes.Brightness(2, 100),
            "level 255 is the top row; a dot's brightness grows with its share of the column, full when all of it");
        var grey = Frame(64, 36, (_, _) => (128, 128, 128));
        var (wave, waveWidth, waveHeight) = VideoScopes.Render(VideoScopeKind.LumaWaveform, grey, 64, 36);
        expect(VideoScopes.ColumnsOf(10, 64, 384) == (60, 66) && VideoScopes.ColumnsOf(10, 256, 128) == (5, 6) && VideoScopes.PixelsPerColumn(64, 36, 384) == 36
               && VideoScopes.PixelsPerColumn(456, 256, 384) == 304,
            "a frame narrower than the scope spreads each column over the scope's (a 9:16 frame leaves none empty); a wider one shares them out");
        expect(waveWidth == VideoScopes.WaveformColumns && waveHeight == 256 && Rgb(wave[VideoScopes.RowOf(128) * waveWidth + 10]) == (204, 255, 216)
               && Rgb(wave[VideoScopes.RowOf(100) * waveWidth + 10]) == (0x10, 0x10, 0x14) && Rgb(wave[VideoScopes.RowOf(0) * waveWidth + 10]) == (0x34, 0x34, 0x40),
            "the Luma Waveform of flat grey 128: 384 x 256, a bright line on row 127, the ground elsewhere, the graticule at 0");
        var (hist, histWidth, _) = VideoScopes.Render(VideoScopeKind.Histogram, grey, 64, 36);
        expect(histWidth == 256 && Rgb(hist[128]) == (0xE6, 0xE6, 0xE6) && Rgb(hist[127]) is (0x10, 0x10, 0x14) or (0x34, 0x34, 0x40),
            "its Histogram: 256 x 256, level 128 a full white bar (the channels over the luma), its neighbour empty");
        var (paradePicture, paradeWidth, _) = VideoScopes.Render(VideoScopeKind.RgbParade, red, 10, 10);
        var (redTop, greenBottom) = (Rgb(paradePicture[VideoScopes.RowOf(255) * paradeWidth + 3]), Rgb(paradePicture[VideoScopes.RowOf(0) * paradeWidth + VideoScopes.ParadeColumns + VideoScopes.ParadeGap + 3]));
        expect(paradeWidth == VideoScopes.ParadeWidth && redTop.R == 255 && redTop.G < 100 && greenBottom.G == 255 && greenBottom.R < 100,
            $"the RGB Parade of flat red: {VideoScopes.ParadeWidth} wide, the red third lit red at the top, the green third lit green at the bottom");
    }

    // ----- video-scopes-pane -----

    private static void ScopesPane(Action<bool, string> expect)
    {
        // The pane's work comes back to the interface's thread through the dispatcher's context, as it does in the window.
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var vm = new MainViewModel();
        var captures = 0;
        var grey = Frame(64, 36, (_, _) => (128, 128, 128));
        var pane = new VideoScopesPane(vm, () =>
        {
            captures++;
            return Task.FromResult<(byte[] Bgra, int Width, int Height)?>((grey, 64, 36));
        });
        var window = new Window { Left = -6000, Top = -6000, Width = 480, Height = 320, Content = pane, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            expect(captures == 0, "made, it captures nothing (Law 1)");
            window.Show();
            Pump(VideoScopesPane.ThrottleMs * 3);
            var picture = Descendants<Image>(pane).FirstOrDefault()?.Source as WriteableBitmap;
            expect(captures == 1 && pane.Kind == VideoScopeKind.LumaWaveform && picture is { PixelWidth: VideoScopes.WaveformColumns, PixelHeight: 256 },
                "shown and paused: one capture, drawn as the Luma Waveform (384 x 256)");

            pane.Kind = VideoScopeKind.Histogram;
            Pump(80);
            picture = Descendants<Image>(pane).FirstOrDefault()?.Source as WriteableBitmap;
            expect(captures == 1 && picture is { PixelWidth: 256, PixelHeight: 256 }, "another scope is drawn from the last frame, without a capture");

            vm.PositionMs = 1000;
            vm.PositionMs = 1040;
            vm.PositionMs = 1080;
            Pump(VideoScopesPane.ThrottleMs * 3);
            expect(captures == 2, "three moves of the playhead in a burst: one capture");

            vm.IsPlaying = true;
            vm.PositionMs = 2000;
            Pump(VideoScopesPane.ThrottleMs * 3);
            var note = Descendants<TextBlock>(pane).FirstOrDefault(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Scope Status");
            expect(captures == 2 && note is { Visibility: Visibility.Visible, Text: "Updates when paused: this is the last paused frame." },
                "while playing: no capture, the last picture kept and \"Updates when paused\" said");
            vm.IsPlaying = false;
            Pump(VideoScopesPane.ThrottleMs * 3);
            expect(captures == 3 && note?.Visibility == Visibility.Collapsed, "paused again: captured once more, the note gone");

            window.Content = null;
            Pump(40);
            vm.PositionMs = 3000;
            Pump(VideoScopesPane.ThrottleMs * 3);
            expect(captures == 3, "out of the window (a dock rebuild, a tab behind): nothing is captured");
            window.Content = pane;
            Pump(VideoScopesPane.ThrottleMs * 3);
            expect(captures == 4, "back in: captured once");
        }
        finally
        {
            vm.IsPlaying = false;
            window.Close();
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                yield return found;
            foreach (var deeper in Descendants<T>(child))
                yield return deeper;
        }
    }
}
