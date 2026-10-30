using System.Windows;
using HandPegApp.Services.Audio;

namespace HandPegApp;

// Settings > Timeline & Player > Audio playback (Pass 5.0-AudioAnd3D, 5.0C): the sound engine's output device, buffer and
// sync offset. The buffer and the offset are bound to the copy of the settings being edited; the device list is asked of
// Windows off the interface's thread the first time the section is shown (the audio service may take a moment to answer).
public partial class SettingsWindow
{
    private bool _audioDevicesListed;

    private async void AudioEngineSection_Loaded(object sender, RoutedEventArgs e)
    {
        if (_audioDevicesListed)
            return;
        _audioDevicesListed = true;

        // System Default at once, so the box is never empty while the list comes.
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

        // A device chosen earlier that is not connected now still shows, so it is not silently replaced.
        if (saved.Length > 0 && devices.All(d => !string.Equals(d.Id, saved, StringComparison.OrdinalIgnoreCase)))
            listed.Add(new Choice(saved, "Chosen device (not connected)"));

        AudioDeviceBox.ItemsSource = listed;
        ShowSavedDevice();
    }

    // The box shows the setting again from its binding (new items do not select the bound value by themselves).
    private void ShowSavedDevice() =>
        System.Windows.Data.BindingOperations.GetBindingExpression(AudioDeviceBox, System.Windows.Controls.Primitives.Selector.SelectedValueProperty)?.UpdateTarget();
}
