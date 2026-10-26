using CommunityToolkit.Mvvm.ComponentModel;

namespace HandPegApp.Models;

/// <summary>One control the playback bar can show, and whether it does.</summary>
/// <param name="editorOnly">Only Editor Mode has it: Encoder Mode's bar never shows it, and its settings do not list it.</param>
public sealed partial class PlaybackControlOption(string id, string label, string group, string tip, bool editorOnly = false) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;

    /// <summary>The heading it is listed under in the bar's settings.</summary>
    public string Group { get; } = group;

    public string Tip { get; } = tip;

    public bool EditorOnly { get; } = editorOnly;

    [ObservableProperty] private bool _isShown;
}

/// <summary>
/// Which controls the playback bar shows, chosen from its settings button, as editors' viewers let one choose
/// (a viewer's button editor, its viewer options). The bar binds each control's visibility to this
/// by name (<c>PlaybackControls[Loop]</c>). Saved per mode as the names of the controls shown.
/// </summary>
public sealed class PlaybackControlSet : ObservableObject
{
    private readonly Dictionary<string, PlaybackControlOption> _byId;

    public PlaybackControlSet()
    {
        Options =
        [
            new("JumpStart", "Go to Start", "Transport", "Jump to the first frame of the sequence (Home)."),
            new("PrevEdit", "Previous Edit Point", "Transport", "Jump back to the nearest start or end of a clip or cut segment."),
            new("PrevIFrame", "Previous I-Frame", "Transport", "Jump back to the I-frame before the playhead."),
            new("StepBack", "Step Back One Frame", "Transport", "One frame back (Left)."),
            new("PlayReverse", "Play Backwards", "Transport", "Plays backwards, faster with each press (J)."),
            new("StepForward", "Step Forward One Frame", "Transport", "One frame on (Right)."),
            new("NextIFrame", "Next I-Frame", "Transport", "Jump to the I-frame after the playhead."),
            new("NextEdit", "Next Edit Point", "Transport", "Jump on to the nearest start or end of a clip or cut segment."),
            new("JumpEnd", "Go to End", "Transport", "Jump to the last frame of the sequence (End)."),
            new("Loop", "Loop Playback", "Transport", "Playback starts again from the beginning at the end: of the selected cut segment, or of the whole sequence."),
            new("MarkIn", "Add Start Point  [", "Cut segments", "A new cut segment begins at the playhead (I)."),
            new("MarkOut", "Add Stop Point  ]", "Cut segments", "The segment begun with the start point ends at the playhead (O)."),
            new("GoToIn", "Go to Start Point", "Cut segments", "Jump to the start point being set, or the start of the selected cut segment."),
            new("GoToOut", "Go to Stop Point", "Cut segments", "Jump to the end of the selected cut segment."),
            new("PlayInOut", "Play Start to Stop", "Cut segments", "Plays the selected cut segment (or from the start point being set to the playhead) and stops at its end."),
            new("Split", "Split at Playhead", "Cut segments", "Cuts the selected block in two at the playhead (S).", editorOnly: true),
            new("AddMarker", "Add Marker", "Markers", "Puts a marker at the playhead (M). Markers can become the export's chapters."),
            new("PrevMarker", "Previous Marker", "Markers", "Jump back to the marker before the playhead (Ctrl+Shift+M)."),
            new("NextMarker", "Next Marker", "Markers", "Jump on to the marker after the playhead (Shift+M)."),
            new("LivePreview", "Live Preview", "Viewer", "Runs the export's filters on the picture as it plays.", editorOnly: true),
            new("SafeMargins", "Safe Margins", "Viewer", "Outlines the action-safe (90%) and title-safe (80%) areas of the frame over the picture."),
            new("ExportFrame", "Export Frame", "Viewer", "Saves the frame as it is shown, as a PNG beside the output."),
            new("RenderPreview", "Render Preview", "Viewer", "Encodes a few seconds as the export will look, and plays them.", editorOnly: true),
            new("Reload", "Reload Player", "Viewer", "For when the player has stopped answering."),
            new("Timecode", "Timecode", "Viewer", "The playhead's time and the sequence's length."),
            new("Mute", "Mute", "Sound", "Silences the player (not the export)."),
            new("Volume", "Volume", "Sound", "The player's volume."),
            new("Speed", "Playback Speed", "Sound", "How fast the player plays (0.25x to 2x)."),
            new("TimelineHeight", "Timeline Height", "Sound", "How tall the master timeline is."),
        ];
        _byId = Options.ToDictionary(o => o.Id);
        foreach (var option in Options)
        {
            option.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged("Item[]");
                Changed?.Invoke();
            };
        }
    }

    public IReadOnlyList<PlaybackControlOption> Options { get; }

    /// <summary>Raised when a control is shown or hidden, so the choice can be saved.</summary>
    public event Action? Changed;

    /// <summary>Whether the control of this name is shown.</summary>
    public bool this[string id] => _byId.TryGetValue(id, out var option) && option.IsShown;

    // What a mode starts with: the bar as it was, and in Editor Mode the start/stop and marker buttons too.
    private static readonly string[] Common = ["PrevIFrame", "StepBack", "StepForward", "NextIFrame", "LivePreview", "RenderPreview", "Reload", "Volume", "Speed", "TimelineHeight"];
    private static readonly string[] EditorExtras = ["MarkIn", "MarkOut", "AddMarker"];

    /// <summary>Shows the controls named in a saved list; null for the mode's own defaults.</summary>
    public void Load(string? saved, bool editorMode)
    {
        var shown = saved is null
            ? new HashSet<string>(editorMode ? Common.Concat(EditorExtras) : Common)
            : new HashSet<string>(saved.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var changed = Changed;
        Changed = null;
        try
        {
            foreach (var option in Options)
                option.IsShown = shown.Contains(option.Id);
        }
        finally
        {
            Changed = changed;
        }
    }

    /// <summary>The names of the controls shown, to save.</summary>
    public string Save() => string.Join(",", Options.Where(o => o.IsShown).Select(o => o.Id));
}
