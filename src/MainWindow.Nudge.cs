using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using HandPegApp.Models;

namespace HandPegApp;

// ----- The arrow keys on the Canvas and the player (5.1-TrackEqualityAndVram, the user's directive) -----
// With the Canvas or the player under the pointer, or a Canvas box holding the keyboard, the arrow keys move what is selected. A press is
// exactly one pixel of the output (ten with Shift), however quick. Held, it waits a moment, then gathers speed up to a limit: a smooth
// glide on the frame's clock, not the keyboard's repeat, whose rate is the system's. One step of Undo for a run of nudges.
public partial class MainWindow
{
    // How long a key is down before it counts as held; how fast it starts and how fast it gets, in pixels of the output a second; how
    // long the ramp takes, and how much Shift multiplies it all.
    private const double NudgeHoldSeconds = 0.28, NudgeLeastSpeed = 40, NudgeMostSpeed = 640, NudgeRampSeconds = 1.1, NudgeShift = 3;

    private readonly HashSet<Key> _nudgeKeys = [];
    private DispatcherTimer? _nudgeTimer;
    private long _nudgeSince, _nudgeLast;
    private double _nudgeCarryX, _nudgeCarryY;
    private LayoutNudge? _nudgeBox;

    /// <summary>An arrow key for what is selected: true when it was taken (the repeats of one held as well).</summary>
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
        // The keyboard is asked, not told: a key let go while another window had it would otherwise glide on for ever.
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

        // Eased in: fine control at first, and only then the long move.
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

    // Whether the pointer is over a surface that arranges layers, with something chosen on it to move.
    private bool NudgeHasTargets() =>
        (LayoutCanvas.IsMouseOver || (PlayerLayoutCanvas.Visibility == Visibility.Visible && (PlayerOverlay.IsMouseOver || VideoView.IsMouseOver) && !_playerOutlineHidden))
        && NudgeTargets().Count > 0;

    // What moves: every clip selected together, else the clip selected. The picture must have a place of its own to move.
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
            // Whole pixels, exactly: no snapping to the guides.
            var (least, most) = layer.IsCaptions ? (0.0, 1.0) : (-2.0, 2.0);
            layer.PositionX = Math.Clamp(layer.PositionX + deltaX / frameWidth, least, most);
            layer.PositionY = Math.Clamp(layer.PositionY + deltaY / frameHeight, least, most);
        }
    }
}
