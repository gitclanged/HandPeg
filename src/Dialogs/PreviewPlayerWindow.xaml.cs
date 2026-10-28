using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace HandPegApp;

/// <summary>
/// A small player for a rendered preview clip: play, pause, scrub. Closing it releases the clip and deletes it.
/// </summary>
public partial class PreviewPlayerWindow : Window
{
    private readonly string _path;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private TimeSpan _duration;
    private bool _isPlaying;
    private bool _isScrubbing;
    private bool _updatingSlider;

    public PreviewPlayerWindow(string path)
    {
        InitializeComponent();
        _path = path;
        _timer.Tick += (_, _) => ShowPosition();

        Loaded += (_, _) =>
        {
            Player.Source = new Uri(_path);
            Play();
        };
        Closed += (_, _) => Release();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        PositionSlider.Maximum = Math.Max(_duration.TotalSeconds, 0.1);
        InfoText.Text = $"{Player.NaturalVideoWidth} x {Player.NaturalVideoHeight}. Shown at reduced size; the real encode uses the full resolution.";
        ShowPosition();
    }

    // At the end it stops on the last frame, ready to be played again from the start.
    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Pause();
        Player.Position = TimeSpan.Zero;
        ShowPosition();
    }

    private void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e) =>
        InfoText.Text = $"The preview could not be played: {e.ErrorException.Message}";

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
            Pause();
        else
            Play();
    }

    private void Play()
    {
        Player.Play();
        _isPlaying = true;
        _timer.Start();
        PlayPauseButton.Content = "Pause";
    }

    private void Pause()
    {
        Player.Pause();
        _isPlaying = false;
        _timer.Stop();
        PlayPauseButton.Content = "Play";
    }

    private void ShowPosition()
    {
        var position = Player.Position;
        TimeText.Text = $"{Format(position)} / {Format(_duration)}";

        // While the thumb is being dragged it belongs to the user.
        if (_isScrubbing)
            return;

        _updatingSlider = true;
        PositionSlider.Value = position.TotalSeconds;
        _updatingSlider = false;
    }

    private static string Format(TimeSpan time) => time.ToString(@"mm\:ss\.f");

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider)
            return;

        Player.Position = TimeSpan.FromSeconds(e.NewValue);
        TimeText.Text = $"{Format(Player.Position)} / {Format(_duration)}";
    }

    private void PositionSlider_DragStarted(object sender, DragStartedEventArgs e) => _isScrubbing = true;

    private void PositionSlider_DragCompleted(object sender, DragCompletedEventArgs e) => _isScrubbing = false;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Stops playback, lets go of the file, and removes it.</summary>
    private void Release()
    {
        _timer.Stop();
        Player.Stop();
        Player.Close();
        Player.Source = null;

        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still held for a moment; the session folder it lives in is removed when the application closes.
        }
    }
}
