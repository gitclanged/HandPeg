using System.Windows;
using System.Windows.Shell;
using HandPegApp.ViewModels;

namespace HandPegApp.Shell;

/// <summary>A window's <see cref="Window.TaskbarItemInfo"/> as the view model's <see cref="ITaskbarProgress"/>; nothing while it has none.</summary>
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
