using System.Windows;
using System.Windows.Shell;
using HandPegApp.ViewModels;

namespace HandPegApp.Shell;

/// <summary><see cref="ITaskbarProgress"/> over a window's <see cref="Window.TaskbarItemInfo"/>; no-op when absent.</summary>
internal sealed class WindowTaskbarProgress(Window window) : ITaskbarProgress
{
    public void Show(TaskbarItemProgressState state, double fraction)
    {
        if (window.TaskbarItemInfo is not { } taskbar)
            return;

        taskbar.ProgressState = state;
        taskbar.ProgressValue = fraction;
    }
}
