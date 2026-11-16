using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace HandPegApp;

/// <summary>
/// Hover-rest flyout (Settings, Add Media, Add Track): opens after the pointer rests on the button for <see cref="OpenDelayMs"/>, so
/// passing over never flashes it; stays open while the pointer is on the button or flyout (and, with <see cref="HoldsWhileFocused"/>,
/// while the flyout has keyboard focus); closes <see cref="CloseDelayMs"/> after leaving both, allowing time to cross between them. The
/// flyout panel also tracks the pointer, so the button losing it to the popup window never closes the flyout.
/// </summary>
public sealed class HoverFlyout
{
    /// <summary>Hover rest on the button before opening, ms.</summary>
    public const int OpenDelayMs = 180;

    /// <summary>Delay after the pointer leaves both button and flyout before closing, ms.</summary>
    public const int CloseDelayMs = 260;

    private readonly Popup _popup;
    private readonly DispatcherTimer _open = new() { Interval = TimeSpan.FromMilliseconds(OpenDelayMs) };
    private readonly DispatcherTimer _close = new() { Interval = TimeSpan.FromMilliseconds(CloseDelayMs) };

    /// <param name="host">Hover target: the button, or the grid containing it and the popup.</param>
    /// <param name="panel">Popup content (its child); keeps the flyout open while hovered.</param>
    public HoverFlyout(FrameworkElement host, Popup popup, FrameworkElement panel)
        : this([host], popup, panel)
    {
    }

    /// <summary>One flyout shared by several hosts (e.g. the same button in two places); opens from whichever is hovered.</summary>
    public HoverFlyout(IReadOnlyList<FrameworkElement> hosts, Popup popup, FrameworkElement panel)
    {
        _popup = popup;
        (OpenedFrom, _resting) = (hosts[0], hosts[0]);
        foreach (var host in hosts)
        {
            host.MouseEnter += (_, _) =>
            {
                _close.Stop();
                if (!popup.IsOpen)
                {
                    _resting = host;
                    _open.Start();
                }
            };
            host.MouseLeave += (_, _) =>
            {
                _open.Stop();
                Left();
            };
        }

        panel.MouseEnter += (_, _) => _close.Stop();
        panel.MouseLeave += (_, _) => Left();
        panel.LostKeyboardFocus += (_, _) =>
        {
            if (!panel.IsKeyboardFocusWithin)
                Left();
        };
        _open.Tick += (_, _) =>
        {
            _open.Stop();
            if (_resting.IsMouseOver && CanOpen?.Invoke() != false)
                Open(_resting);
        };
        _close.Tick += (_, _) =>
        {
            _close.Stop();
            if (!hosts.Any(h => h.IsMouseOver) && !panel.IsMouseOver && !(HoldsWhileFocused && panel.IsKeyboardFocusWithin) && HoldsWhile?.Invoke() != true)
                popup.IsOpen = false;
        };
    }

    // Most recently hovered host.
    private FrameworkElement _resting;

    /// <summary>Host the flyout was last opened from (used by <see cref="Opening"/> for placement).</summary>
    public FrameworkElement OpenedFrom { get; private set; }

    /// <summary>Keep open while the flyout has keyboard focus (e.g. a text box being edited).</summary>
    public bool HoldsWhileFocused { get; init; }

    /// <summary>Extra keep-open predicate (e.g. a row's context menu is open, or the pointer is over a child window).</summary>
    public Func<bool>? HoldsWhile { get; init; }

    /// <summary>Gate for hover-opening (e.g. suppressed while a click-opened surface is open, or the Layout selector during a tab switch).</summary>
    public Func<bool>? CanOpen { get; init; }

    /// <summary>Signals that an external hold ended; closes after the delay unless the pointer is on the button or flyout.</summary>
    public void Release() => Left();

    /// <summary>Invoked just before opening, for placement or content refresh.</summary>
    public Action? Opening { get; init; }

    /// <summary>Opens immediately (e.g. from the keyboard), with no delay.</summary>
    /// <param name="from">Host to open from; defaults to the last one used.</param>
    public void Open(FrameworkElement? from = null)
    {
        _open.Stop();
        _close.Stop();
        OpenedFrom = from ?? OpenedFrom;
        Opening?.Invoke();
        _popup.IsOpen = true;
    }

    /// <summary>Closes immediately (after a selection or Escape).</summary>
    public void Close()
    {
        _open.Stop();
        _close.Stop();
        _popup.IsOpen = false;
    }

    // Pointer left the button or flyout: arm the close delay (cancelled if it returns to either).
    private void Left()
    {
        if (!_popup.IsOpen)
            return;

        _close.Stop();
        _close.Start();
    }
}
