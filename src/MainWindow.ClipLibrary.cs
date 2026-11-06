using System.Globalization;
using HandPegApp.Models;

namespace HandPegApp;

// ----- The Personal Clip Library on a clip's menu (Pass 5.2C, Services/ClipLibrary.cs) -----
public partial class MainWindow
{
    /// <summary>The media browser lists the Clip Library again when a clip goes into it.</summary>
    private void InitializeClipLibrary() => _viewModel.ClipLibraryChanged += () => MediaBrowser.LibraryChanged();

    /// <summary>"Save Clip to Personal Clip Library...": a title and tags asked for, then the span kept in the background.</summary>
    private void SaveToClipLibrary(Layer clip)
    {
        if (_viewModel.ClipOf(clip) is not { } model)
            return;

        var what = string.Create(CultureInfo.InvariantCulture, $"{model.Duration:0.#} seconds of {System.IO.Path.GetFileName(model.SourcePath)}, kept as it is (nothing encoded again) in Documents\\HandPeg\\Clips");
        if (_viewModel.FocalIn(clip) is { } focal)
            what += string.Create(CultureInfo.InvariantCulture, $", with the focus of its highlight ({focal.X:0%} across, {focal.Y:0%} down).");
        else
            what += ".";

        var dialog = new SaveToClipLibraryDialog(clip.Name, what) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        _ = _viewModel.SaveToClipLibraryAsync(clip, dialog.ClipTitle, dialog.Tags);
    }
}
