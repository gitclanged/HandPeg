using System.Windows.Controls;

namespace HandPegApp.Views;

/// <summary>
/// The Render Queue as a dock pane (5.4C): the batch of encodes. Bound to the main window's view model (the dock gives every pane the
/// window's data), so it and the Export tab stay in step; it holds no state of its own, and is made only when first shown.
/// </summary>
public partial class RenderQueuePane : UserControl
{
    public RenderQueuePane() => InitializeComponent();
}
