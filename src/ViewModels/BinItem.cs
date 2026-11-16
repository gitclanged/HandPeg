using HandPegApp.Models;

namespace HandPegApp.ViewModels;

/// <summary>Recycle bin drawer entry for a deleted clip.</summary>
public sealed class BinItem(DeletedClip clip, string name, string timeText, System.Windows.Media.ImageSource? preview)
{
    public DeletedClip Clip { get; } = clip;

    public string Name { get; } = name;

    /// <summary>Original timeline range.</summary>
    public string TimeText { get; } = timeText;

    /// <summary>Thumbnail (video frame or waveform) when available.</summary>
    public System.Windows.Media.ImageSource? Preview { get; } = preview;
}
