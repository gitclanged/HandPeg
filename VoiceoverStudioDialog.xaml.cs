using System.ComponentModel;
using System.Windows;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using Microsoft.Win32;

namespace HandPegApp;

/// <summary>
/// Where the voiceover is made: recorded from a microphone with Record, Pause and Stop, or brought in from
/// a file, then trimmed, placed and set to a level. The voiceover belongs to the main window's settings,
/// and the encode takes it as a second input.
/// </summary>
public partial class VoiceoverStudioDialog : Window
{
    private readonly MainViewModel _viewModel;

    public VoiceoverStudioDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ThemeManager.ApplyTitleBar(this);

        Loaded += (_, _) =>
        {
            if (_viewModel.Microphones.Count == 0)
                _viewModel.RefreshMicrophonesCommand.Execute(null);
        };
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the voiceover",
            Filter = "Audio|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.ImportVoiceoverAsync(dialog.FileName);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Closing in the middle of a recording finishes it, so that nothing said is lost.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.StopVoiceoverCommand.CanExecute(null))
            _viewModel.StopVoiceoverCommand.Execute(null);

        base.OnClosing(e);
    }
}
