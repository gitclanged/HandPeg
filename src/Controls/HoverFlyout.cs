using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace HandPegApp;

/// <summary>
/// A flyout that opens while the pointer rests on a button (the Settings button's pattern, also Add Media and Add
/// Layer): it opens once the pointer has stayed on the button for <see cref="OpenDelayMs"/>, so moving across the
/// button never flashes it; it stays while the pointer is on the button or on the flyout (and, when
/// <see cref="HoldsWhileFocused"/>, while the flyout has the keyboard); and it closes <see cref="CloseDelayMs"/> after
/// the pointer has left both, which gives it time to cross from one to the other. The flyout's own panel answers the
/// pointer too, so the button losing the pointer to the flyout's window never closes it.
/// </summary>
public sealed class HoverFlyout
{
    /// <summary>How long the pointer rests on the button before the flyout opens.</summary>
    public const int OpenDelayMs = 180;

    /// <summary>How long after the pointer left both the button and the flyout it closes.</summary>
    public const int CloseDelayMs = 260;

    private readonly Popup _popup;
    private readonly DispatcherTimer _open = new() { Interval = TimeSpan.FromMilliseconds(OpenDelayMs) };
    private readonly DispatcherTimer _close = new() { Interval = TimeSpan.FromMilliseconds(CloseDelayMs) };

    /// <param name="host">What the pointer rests on: the button, or the grid holding it and the popup.</param>
    /// <param name="panel">The popup's content (its child), which keeps it open while the pointer is on it.</param>
    public HoverFlyout(FrameworkElement host, Popup popup, FrameworkElement panel)
        : this([host], popup, panel)
    {
    }

    /// <summary>One flyout for several buttons (the same button in two places): it opens from whichever the pointer rests on.</summary>
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

    // The button the pointer came to rest on last.
    private FrameworkElement _resting;

    /// <summary>The button the flyout was last opened from (for <see cref="Opening"/> to place it by).</summary>
    public FrameworkElement OpenedFrom { get; private set; }

    /// <summary>Stays open while something in the flyout has the keyboard (a box being typed into).</summary>
    public bool HoldsWhileFocused { get; init; }

    /// <summary>Stays open while this says so: a menu opened from one of its rows, a window of its own the pointer is on.</summary>
    public Func<bool>? HoldsWhile { get; init; }

    /// <summary>Whether resting on the button may open it now: not while something the button opened by a click is open (the Layout button's menu).</summary>
    public Func<bool>? CanOpen { get; init; }

    /// <summary>What held it open has let go: it closes a moment later, unless the pointer is on the button or the flyout.</summary>
    public void Release() => Left();

    /// <summary>Called just before the flyout opens: to place it, or to bring what it shows up to date.</summary>
    public Action? Opening { get; init; }

    /// <summary>Opens it now (from the keyboard, say), with no wait.</summary>
    /// <param name="from">The button it opens from; the one it last opened from when not said.</param>
    public void Open(FrameworkElement? from = null)
    {
        _open.Stop();
        _close.Stop();
        OpenedFrom = from ?? OpenedFrom;
        Opening?.Invoke();
        _popup.IsOpen = true;
    }

    /// <summary>Closes it now (a choice was made in it, Escape).</summary>
    public void Close()
    {
        _open.Stop();
        _close.Stop();
        _popup.IsOpen = false;
    }

    // The pointer left the button or the flyout: it closes a moment later, unless it comes back to either.
    private void Left()
    {
        if (!_popup.IsOpen)
            return;

        _close.Stop();
        _close.Start();
    }
}
