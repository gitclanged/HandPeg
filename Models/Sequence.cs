namespace HandPegApp.Models;

/// <summary>
/// A further clip of the main video: what a split leaves after the first. Where it starts on the sequence,
/// how long it stays, and how far into the file it begins, all in seconds.
/// </summary>
public sealed record MainPiece(double Start, double Duration, double Offset);

/// <summary>
/// One clip of the main video as it lies on the sequence: where it starts and ends there, and how far into the
/// file it begins, all in seconds. Index 0 is the row's own clip; the others follow in the order they were made.
/// </summary>
public readonly record struct MainClip(int Index, double Start, double End, double Offset);

/// <summary>
/// A clip that was deleted from the timeline and waits in the recycle bin: a layer's clip as it was (with
/// its place in the list), or a clip of the main video.
/// </summary>
public sealed record DeletedClip(LayerState? Layer, MainPiece? Main, int Index);

/// <summary>A deleted clip as the recycle bin's drawer lists it.</summary>
public sealed class BinItem(DeletedClip clip, string name, string timeText, System.Windows.Media.ImageSource? preview)
{
    public DeletedClip Clip { get; } = clip;

    public string Name { get; } = name;

    /// <summary>When it was on the timeline, from where to where.</summary>
    public string TimeText { get; } = timeText;

    /// <summary>A picture of it: a frame of its video or the shape of its sound, when one has been made.</summary>
    public System.Windows.Media.ImageSource? Preview { get; } = preview;
}
