using System.IO;
using System.Windows;
using System.Windows.Controls;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegApp;

/// <summary>
/// Just-in-time dependency download for a missing tool or speech model (first run installs only essentials by default). Lists
/// items and sizes, downloads exactly those, then closes so the triggering action continues. Built in code: heading, list, one
/// progress bar, two buttons.
/// </summary>
public sealed class DependencyPromptWindow : Window
{
    private readonly DependencyRequest _request;
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _download = new() { Content = "Download", MinWidth = 96, IsDefault = true };
    private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 96, Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
    private CancellationTokenSource? _cancellation;

    public DependencyPromptWindow(DependencyRequest request)
    {
        _request = request;
        Title = "HandPeg";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = $"{request.Purpose} needs a download", FontSize = 15, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = $"Not installed yet. It is downloaded once, into {AppPaths.Deps}, and {request.Purpose} then carries on by itself.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 8),
        });

        foreach (var tool in request.Tools)
            panel.Children.Add(new TextBlock { Text = $"•  {DependencyUpdater.DisplayName(tool)}", Margin = new Thickness(6, 2, 0, 2) });
        foreach (var model in request.Models)
        {
            var name = model == DependencyUpdater.VadModel ? "Silence detection model (under 1 MB)" : $"Speech model {model} ({Megabytes(DependencyUpdater.ApproximateModelBytes(model))})";
            panel.Children.Add(new TextBlock { Text = $"•  {name}", Margin = new Thickness(6, 2, 0, 2) });
        }

        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        panel.Children.Add(_bar);
        panel.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_download);
        buttons.Children.Add(_cancel);
        panel.Children.Add(buttons);
        Content = panel;

        _download.Click += async (_, _) => await DownloadAsync();

        // Closing mid-download (Cancel or title bar) stops it; incomplete items are left untouched.
        Closed += (_, _) => _cancellation?.Cancel();
    }

    private static string Megabytes(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824.0:0.0} GB" : $"{bytes / 1048576.0:0} MB";

    private async Task DownloadAsync()
    {
        if (_cancellation is not null)
            return;

        var cancellation = _cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _download.IsEnabled = false;
        (_bar.Visibility, _bar.Value, _bar.IsIndeterminate) = (Visibility.Visible, 0, true);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _status.Text = "Checking what is available...";

        try
        {
            // Per-item sizes so the single progress bar tracks bytes, not item count.
            var work = new List<(string Id, string Name, bool IsModel, RemoteAsset? Asset, double Size)>();
            foreach (var tool in _request.Tools.Where(t => DependencyUpdater.GetLocalState(t) == DependencyState.Missing))
            {
                var asset = await DependencyUpdater.GetLatestAsync(tool, token);
                work.Add((tool, DependencyUpdater.DisplayName(tool), false, asset, Math.Max(asset.Size, 1048576)));
            }

            foreach (var model in _request.Models.Where(m => !File.Exists(DependencyUpdater.GetWhisperModelPath(m))))
                work.Add((model, model, true, null, Math.Max(DependencyUpdater.ApproximateModelBytes(model), 1048576)));

            var (total, arrived) = (work.Sum(w => w.Size), 0.0);
            _bar.IsIndeterminate = false;
            foreach (var item in work)
            {
                var before = arrived;
                var progress = new Progress<InstallProgress>(p =>
                {
                    _bar.Value = (before + Math.Clamp(p.Fraction, 0, 1) * item.Size) / total;
                    _status.Text = $"{item.Name}: {p.Text}";
                });

                _status.Text = $"Downloading {item.Name}...";

                // Thread pool: streaming hash verification stays off the UI thread.
                if (item.IsModel)
                    await Task.Run(() => DependencyUpdater.InstallModelAsync(item.Id, progress, token), token);
                else
                    await Task.Run(() => DependencyUpdater.InstallToolAsync(item.Id, item.Asset!, progress, token), token);

                arrived += item.Size;
                _bar.Value = arrived / total;
            }

            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // Window already closed: nothing to report or close.
        }
        catch (Exception ex) when (DependencyUpdater.IsInstallFailure(ex) || ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Downloading what {_request.Purpose} needs failed", ex);
            _status.SetResourceReference(TextBlock.ForegroundProperty, "WarningTextBrush");
            _status.Text = $"The download did not finish: {ex.Message} Nothing that was working has been changed. Check the connection and press Retry.";
            (_download.Content, _download.IsEnabled, _bar.Visibility) = ("Retry", true, Visibility.Collapsed);
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
                _cancellation = null;
            cancellation.Dispose();
        }
    }
}
