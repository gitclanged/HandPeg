using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using HandPegApp.Controls;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp;

// Window side of the hidden developer flight recorder (DevFlightRecorder): logs every key and click in any HandPeg window with
// playhead and selection, and snapshots the open project after each edit, undo step or load. Handlers are always registered but
// no-op while the recorder is off (enablement is decided after Frame 1).
public partial class MainWindow
{
    private Debounce? _flightSnapshot;

    private void InitializeDevFlight()
    {
        DevFlightRecorder.Where = () => string.Create(CultureInfo.InvariantCulture,
            $"playhead {_viewModel.PositionMs / 1000:0.000}s, selected {SelectedForFlight()}");

        EventManager.RegisterClassHandler(typeof(Window), PreviewKeyDownEvent, new KeyEventHandler((sender, e) =>
        {
            if (DevFlightRecorder.IsEnabled && !e.IsRepeat)
                DevFlightRecorder.RecordInput("key", $"{Keyboard.Modifiers} {(e.Key == Key.System ? e.SystemKey : e.Key)} in {Describe(sender, e.OriginalSource)}");
        }), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), PreviewMouseDownEvent, new MouseButtonEventHandler((sender, e) =>
        {
            if (DevFlightRecorder.IsEnabled)
            {
                var at = sender is IInputElement window ? e.GetPosition(window) : default;
                DevFlightRecorder.RecordInput("mouse", string.Create(CultureInfo.InvariantCulture,
                    $"{e.ChangedButton} x{e.ClickCount} at {at.X:0},{at.Y:0} on {Describe(sender, e.OriginalSource)}"));
            }
        }), handledEventsToo: true);

        _viewModel.TimelineChanged += SnapshotForFlight;
        _viewModel.TimelineRestored += SnapshotForFlight;
        _viewModel.MediaLoaded += _ => SnapshotForFlight();
    }

    // Input target description: window title, element type and Name (or UIA name).
    private static string Describe(object sender, object source)
    {
        var where = sender is Window { Title: { Length: > 0 } title } ? title : sender.GetType().Name;
        if (source is not FrameworkElement element)
            return $"{where} / {source.GetType().Name}";

        var name = element.Name is { Length: > 0 } own ? own : AutomationProperties.GetName(element);
        return name is { Length: > 0 } ? $"{where} / {element.GetType().Name} \"{name}\"" : $"{where} / {element.GetType().Name}";
    }

    private string SelectedForFlight() => _selectedClip switch
    {
        Layer layer => $"clip {layer.ClipId} ({layer.Kind}, track {layer.TrackId})",
        null when _playheadVideoSelected => "the video at the playhead",
        null => "nothing",
        var other => other.GetType().Name,
    };

    // After an edit, undo step or load: one snapshot 1.5 s after the last event of a burst (a 30 Hz effect drag yields one, at its end),
    // at ContextIdle. Serializes the live document on the UI thread, since copying it would cost as much as serializing.
    private void SnapshotForFlight()
    {
        if (DevFlightRecorder.IsEnabled)
            (_flightSnapshot ??= new Debounce(TimeSpan.FromMilliseconds(1500), TakeFlightSnapshot, DispatcherPriority.ContextIdle)).Poke();
    }

    private void TakeFlightSnapshot()
    {
        try
        {
            var timeline = ProjectStore.TimelineNode(_viewModel.Document)?.ToJsonString();
            DevFlightRecorder.TakeSnapshot(_viewModel.CurrentProjectPath, [_viewModel.LocalMediaPath, .. _viewModel.MediaFilesInUse()], timeline);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DevFlightRecorder.Record("dev", "snapshot failed: " + ex);
        }
    }
}
