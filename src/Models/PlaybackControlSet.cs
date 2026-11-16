using CommunityToolkit.Mvvm.ComponentModel;

namespace HandPegApp.Models;

/// <summary>One control the playback bar can show, and whether it does.</summary>
public sealed partial class PlaybackControlOption(string id, string label, string group, string tip) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;

    /// <summary>The heading it is listed under in the bar's settings.</summary>
    public string Group { get; } = group;

    public string Tip { get; } = tip;

    [ObservableProperty] private bool _isShown;
}

/// <summary>
/// Visible playback bar controls (Views menu, Playback Bar column). The bar binds visibility by name (<c>PlaybackControls[Loop]</c>);
/// serialized as the list of visible control names.
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
            new("Split", "Split at Playhead", "Cut segments", "Cuts the selected block in two at the playhead (S)."),
            new("AddMarker", "Add Marker", "Markers", "Puts a marker at the playhead (M). Markers can become the export's chapters."),
            new("PrevMarker", "Previous Marker", "Markers", "Jump back to the marker before the playhead (Ctrl+Shift+M)."),
            new("NextMarker", "Next Marker", "Markers", "Jump on to the marker after the playhead (Shift+M)."),
            new("SafeMargins", "Safe Margins", "Viewer", "Outlines the action-safe (90%) and title-safe (80%) areas of the frame over the picture."),
            new("EditOnVideo", "Edit Clips on Video Preview", "Viewer", "Move, resize and turn the selected clip on the player itself."),
            new("Exploded3D", "3D Exploded View", "Viewer", "Turns the player into a 3D view of the layer stack: orbit it, spread the layers apart, drag them in their planes and along the stack (the GPU preview engine)."),
            // Live Encoded Preview button: default visible after 3D View (list version 3 adds it once to older bars); the preview
            // state itself is never saved and starts off.
            new("ExportQualityPreview", "Live Encoded Preview", "Viewer", "Shows the picture as the export's encoder would leave it, live: the encoded frame, a split wipe against the source, or the difference x10 (an approximation by this PC's hardware encoder)."),
            new("ExportFrame", "Export Frame", "Viewer", "Saves the frame as it is shown, as a PNG beside the output."),
            new("Reload", "Reload Player", "Viewer", "For when the player has stopped answering."),
            new("Timecode", "Timecode", "Viewer", "The playhead's time and the sequence's length."),
            // Volume pill includes mute; the retired "Mute" name is ignored on load and dropped on next save.
            new("Volume", "Volume", "Sound", "The player's volume, and its mute: a click on its speaker silences the player (not the export)."),
            new("Speed", "Playback Speed", "Sound", "How fast the player plays (0.25x to 2x)."),
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

    // Default controls. (Retired names such as Live Preview and Render Preview are ignored on load and dropped on save.)
    private static readonly string[] Defaults =
        ["PrevIFrame", "StepBack", "StepForward", "NextIFrame", "Reload", "Volume", "Speed", "AddMarker", "EditOnVideo", "Exploded3D", "ExportQualityPreview"];

    // List version (trailing "@3") and controls introduced per version: older lists gain new controls once (3D Exploded View, Live
    // Encoded Preview), after which the saved choice is respected.
    private const int Version = 3;
    private const string VersionMark = "@";
    private static readonly (string Id, int Since)[] Later = [("Exploded3D", 2), ("ExportQualityPreview", 3)];

    /// <summary>Shows the controls named in a saved list; null for the defaults.</summary>
    public void Load(string? saved)
    {
        var names = saved?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var savedVersion = 1;
        foreach (var name in names)
        {
            if (name.StartsWith(VersionMark, StringComparison.Ordinal) && int.TryParse(name.AsSpan(VersionMark.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version))
                savedVersion = version;
        }

        var shown = saved is null ? new HashSet<string>(Defaults) : new HashSet<string>(names);
        if (saved is not null)
        {
            foreach (var (id, since) in Later)
            {
                if (savedVersion < since)
                    shown.Add(id);
            }
        }
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

    /// <summary>The names of the controls shown, to save, and the list's version last.</summary>
    public string Save() => string.Join(",", Options.Where(o => o.IsShown).Select(o => o.Id).Append(VersionMark + Version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
