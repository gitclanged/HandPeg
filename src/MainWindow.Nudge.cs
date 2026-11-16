using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using HandPegApp.Models;

namespace HandPegApp;

// ----- Arrow-key nudging on the Canvas and player -----
// With the pointer over the Canvas or player, or a Canvas box focused, arrow keys move the selection. A tap moves exactly one output pixel
// (ten with Shift). Holding waits briefly, then accelerates to a cap: a smooth glide on a frame-rate timer rather than the system key
// repeat rate. A run of nudges is one Undo step.
public partial class MainWindow
{
    // Hold delay (s), start and max glide speed (output px/s), ramp duration (s), and Shift speed multiplier.
    private const double NudgeHoldSeconds = 0.28, NudgeLeastSpeed = 40, NudgeMostSpeed = 640, NudgeRampSeconds = 1.1, NudgeShift = 3;

    private readonly HashSet<Key> _nudgeKeys = [];
    private DispatcherTimer? _nudgeTimer;
    private long _nudgeSince, _nudgeLast;
    private double _nudgeCarryX, _nudgeCarryY;
    private LayoutNudge? _nudgeBox;

    /// <summary>Handles an arrow key for the selection; returns true when consumed (including repeats of a held key).</summary>
    private bool TakeNudge(Key key, IInputElement? focused, bool isRepeat)
    {
        if (isRepeat)
            return _nudgeKeys.Contains(key);

        var box = focused is System.Windows.Controls.Primitives.Thumb { Tag: LayoutNudge nudge } ? nudge : null;
        if (box is null && !NudgeHasTargets())
            return false;

        _nudgeBox = box;
        _nudgeKeys.Add(key);
        Nudge(Pixels(key).X * NudgeStep, Pixels(key).Y * NudgeStep);

        _nudgeSince = _nudgeLast = Environment.TickCount64;
        (_nudgeCarryX, _nudgeCarryY) = (0, 0);
        if (_nudgeTimer is null)
        {
            _nudgeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
            _nudgeTimer.Tick += NudgeTimer_Tick;
        }

        _nudgeTimer.Start();
        return true;
    }

    private static double NudgeStep => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;

    private static Point Pixels(Key key) => new(key == Key.Left ? -1 : key == Key.Right ? 1 : 0, key == Key.Up ? -1 : key == Key.Down ? 1 : 0);

    private void NudgeTimer_Tick(object? sender, EventArgs e)
    {
        // Poll key state rather than rely on KeyUp: a release while another window has focus would otherwise glide forever.
        _nudgeKeys.RemoveWhere(k => !Keyboard.IsKeyDown(k));
        if (_nudgeKeys.Count == 0 || !IsActive)
        {
            StopNudge();
            return;
        }

        var now = Environment.TickCount64;
        var held = (now - _nudgeSince) / 1000.0 - NudgeHoldSeconds;
        var seconds = Math.Min((now - _nudgeLast) / 1000.0, 0.05);
        _nudgeLast = now;
        if (held <= 0)
            return;

        // Quadratic ease-in: fine control first, then faster long moves.
        var through = Math.Clamp(held / NudgeRampSeconds, 0, 1);
        var speed = (NudgeLeastSpeed + (NudgeMostSpeed - NudgeLeastSpeed) * through * through) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? NudgeShift : 1);
        var (x, y) = (0.0, 0.0);
        foreach (var key in _nudgeKeys)
            (x, y) = (x + Pixels(key).X, y + Pixels(key).Y);

        (_nudgeCarryX, _nudgeCarryY) = (_nudgeCarryX + x * speed * seconds, _nudgeCarryY + y * speed * seconds);
        var (wholeX, wholeY) = (Math.Truncate(_nudgeCarryX), Math.Truncate(_nudgeCarryY));
        if (wholeX == 0 && wholeY == 0)
            return;

        (_nudgeCarryX, _nudgeCarryY) = (_nudgeCarryX - wholeX, _nudgeCarryY - wholeY);
        Nudge(wholeX, wholeY);
    }

    private void StopNudge()
    {
        _nudgeTimer?.Stop();
        _nudgeKeys.Clear();
        _nudgeBox = null;
    }

    // True when the pointer is over a layout surface (Canvas or player) and there is something to move.
    private bool NudgeHasTargets() =>
        (LayoutCanvas.IsMouseOver || (PlayerLayoutCanvas.Visibility == Visibility.Visible && (PlayerOverlay.IsMouseOver || VideoView.IsMouseOver) && !_playerOutlineHidden))
        && NudgeTargets().Count > 0;

    // Nudge targets: the group selection, else the single selection; only positionable visual clips (or captions).
    private List<Layer> NudgeTargets()
    {
        var chosen = _viewModel.SelectedClips.Count > 0 ? _viewModel.SelectedClips.ToList() : _selectedClip is Layer one ? [one] : [];
        return chosen.Where(l => l is { IsAudio: false, IsEmptyTrack: false, IsBackground: false } && (l.IsCaptions || _viewModel.ClipOf(l) is not null)).ToList();
    }

    private void Nudge(double deltaX, double deltaY)
    {
        var targets = _nudgeBox is null ? NudgeTargets() : [];
        var name = _nudgeBox?.Name ?? (targets.Count == 1 ? targets[0].Name : $"{targets.Count} clips");
        if (Environment.TickCount64 - _nudgedAt > 1000)
            _viewModel.Checkpoint($"nudge {name}");
        _nudgedAt = Environment.TickCount64;

        if (_nudgeBox is { } box)
        {
            box.Move(deltaX, deltaY);
            return;
        }

        var (frameWidth, frameHeight) = ((double)_viewModel.FrameWidth, (double)_viewModel.FrameHeight);
        if (frameWidth <= 0 || frameHeight <= 0)
            return;

        foreach (var layer in targets)
        {
            // Exact whole-pixel moves; no guide snapping.
            var (least, most) = layer.IsCaptions ? (0.0, 1.0) : (-2.0, 2.0);
            layer.PositionX = Math.Clamp(layer.PositionX + deltaX / frameWidth, least, most);
            layer.PositionY = Math.Clamp(layer.PositionY + deltaY / frameHeight, least, most);
        }
    }
}
