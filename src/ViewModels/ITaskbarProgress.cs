using System.Windows.Shell;

namespace HandPegApp.ViewModels;

/// <summary>Progress on the taskbar button of the window that owns a view model (an encode's). Called on that window's thread.</summary>
public interface ITaskbarProgress
{
    /// <summary>Shows <paramref name="state"/> with <paramref name="fraction"/> done (0 to 1).</summary>
    void Show(TaskbarItemProgressState state, double fraction);
}
