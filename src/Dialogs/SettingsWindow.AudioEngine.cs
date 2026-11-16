using System.Windows;
using HandPegApp.Services.Audio;

using HandPegApp.Services;

namespace HandPegApp;

// Settings > Timeline & Player > Audio playback: output device, buffer and sync offset. Buffer and offset bind to the edited settings
// copy; devices are enumerated off the UI thread on first show (the Windows audio service can be slow to respond).
public partial class SettingsWindow
{
    private bool _audioDevicesListed;

    private void AudioEngineSection_Loaded(object sender, RoutedEventArgs e) => Faults.Forget(AudioEngineSection_LoadedAsync(sender, e), nameof(AudioEngineSection_Loaded));

    private async Task AudioEngineSection_LoadedAsync(object sender, RoutedEventArgs e)
    {
        if (_audioDevicesListed)
            return;
        _audioDevicesListed = true;

        // Seed System Default immediately so the box is never empty during enumeration.
        var saved = _settings.AudioOutputDeviceId ?? "";
        AudioDeviceBox.ItemsSource = new List<Choice> { new("", "System Default") };
        ShowSavedDevice();

        IReadOnlyList<AudioDevice> devices;
        try
        {
            devices = await Task.Run(AudioDevices.List);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.AppLog.Write("Settings: the sound outputs could not be listed", ex);
            return;
        }

        var listed = new List<Choice> { new("", devices.FirstOrDefault(d => d.IsDefault) is { } standard ? $"System Default ({standard.Name})" : "System Default") };
        listed.AddRange(devices.Select(d => new Choice(d.Id, d.Name)));

        // Keep a disconnected saved device listed so it is not silently replaced.
        if (saved.Length > 0 && devices.All(d => !string.Equals(d.Id, saved, StringComparison.OrdinalIgnoreCase)))
            listed.Add(new Choice(saved, "Chosen device (not connected)"));

        AudioDeviceBox.ItemsSource = listed;
        ShowSavedDevice();
    }

    // Re-apply the bound selection (adding items does not reselect the bound value).
    private void ShowSavedDevice() =>
        System.Windows.Data.BindingOperations.GetBindingExpression(AudioDeviceBox, System.Windows.Controls.Primitives.Selector.SelectedValueProperty)?.UpdateTarget();
}
