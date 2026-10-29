using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using HandPegApp.Models;
using HandPegApp.Services;

namespace HandPegApp;

// The hidden developer flight recorder's window side (4.4.0, DevFlightRecorder): every key and click in any of HandPeg's
// windows, with the playhead and the selection, and a snapshot of the open project after each edit, undo step or load.
// The handlers are always there and do nothing while the recorder is off (it is decided after the first frame).
public partial class MainWindow
{
    private DispatcherOperation? _flightSnapshot;

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

    // What a key or a click went to: its window, and the element's type and name (or its UIA name).
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
        null when _mainClipSelected => "the main video's clip",
        null => "nothing",
        var other => other.GetType().Name,
    };

    // After an edit, an undo step or a load: one snapshot once the window is idle, however many came at once.
    private void SnapshotForFlight()
    {
        if (!DevFlightRecorder.IsEnabled || _flightSnapshot is { Status: DispatcherOperationStatus.Pending })
            return;

        _flightSnapshot = Dispatcher.BeginInvoke(() =>
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
        }, DispatcherPriority.ContextIdle);
    }
}
