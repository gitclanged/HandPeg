using System.Windows.Controls;

namespace HandPegApp.Views;

/// <summary>
/// Render Queue dock pane (batch encodes). Binds to the main window's view model (the dock passes the window's DataContext), so it
/// stays in sync with the Export tab; stateless, and created only on first show.
/// </summary>
public partial class RenderQueuePane : UserControl
{
    public RenderQueuePane() => InitializeComponent();
}
