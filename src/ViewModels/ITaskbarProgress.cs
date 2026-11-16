using System.Windows.Shell;

namespace HandPegApp.ViewModels;

/// <summary>Taskbar button progress for the VM's owning window (encodes). Called on that window's UI thread.</summary>
public interface ITaskbarProgress
{
    /// <summary>Sets <paramref name="state"/> and progress <paramref name="fraction"/> (0..1).</summary>
    void Show(TaskbarItemProgressState state, double fraction);
}
