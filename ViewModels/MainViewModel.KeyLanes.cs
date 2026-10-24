using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>One animated value of an effect card, as a row of the Keyframes pane stands for it.</summary>
public sealed record EffectKeyRef(EffectInstance Effect, EffectParam Param, string Label);

// The Keyframes pane's rows (see KeyLanesView): All (every motion property at once, as the Key All button keys them), one
// row per motion property, and one per animated effect value. Each row is keyed, moved and deleted on its own; the
// selected one is what the easing box changes. A row stands for a KeyProperty, an EffectKeyRef, or (null) All.
public partial class MainViewModel
{
    private object? _selectedKeyLane;

    /// <summary>The row of the Keyframes pane that is selected: null for All, a KeyProperty, or an EffectKeyRef.</summary>
    public object? SelectedKeyLane
    {
        get => _selectedKeyLane;
        set
        {
            if (Equals(_selectedKeyLane, value))
                return;

            _selectedKeyLane = value;
            OnPropertyChanged();
            RefreshSelectedKey();
        }
    }

    /// <summary>The moment of the playhead on a clip's own clock: what its keyframes are counted in.</summary>
    private double ClipClock(Layer layer) => PositionMs / 1000 - layer.StartTime;

    /// <summary>The keyframe at the playhead on a row of the clip; null when the row has none there.</summary>
    public Keyframe? KeyOnLane(Layer layer, object? lane)
    {
        var time = ClipClock(layer);
        switch (lane)
        {
            case KeyProperty property:
                return layer.GetKeyAt(time, property);
            case EffectKeyRef value:
                foreach (var key in value.Effect.GetKeys(value.Param.Key))
                {
                    if (Math.Abs(key.Time - time) <= Layer.KeyTolerance)
                        return key;
                }

                return null;
            default:
                return layer.GetKeyAt(time);
        }
    }

    /// <summary>The diamond at a row's name: a keyframe at the playhead holding the value there, or the one there taken away.</summary>
    public void ToggleLaneKey(Layer layer, object? lane)
    {
        if (!HasSource)
        {
            StatusText = "Load a video first: a keyframe is set at the playhead.";
            return;
        }

        var time = Math.Max(ClipClock(layer), 0);
        switch (lane)
        {
            case KeyProperty property:
                Checkpoint($"key the {LaneName(lane)} of {layer.Name}");
                Log(layer.ToggleKey(property, time)
                    ? $"Keyed {LaneName(lane)} at {TimeDisplay.Format(PositionMs / 1000)} ({layer.Name})"
                    : $"Removed the {LaneName(lane)} keyframe at {TimeDisplay.Format(PositionMs / 1000)} ({layer.Name})");
                break;

            case EffectKeyRef value:
            {
                var keys = value.Effect.GetKeys(value.Param.Key).ToList();
                var at = keys.FindIndex(k => Math.Abs(k.Time - time) <= Layer.KeyTolerance);
                var now = EffectValueAt(layer, value, time);
                Checkpoint($"key {value.Label} of {layer.Name}");
                if (at >= 0)
                    keys.RemoveAt(at);
                else
                    keys.Add(new Keyframe(Math.Round(time, 3), now));

                using (Layer.Echo())
                {
                    // The last keyframe gone, the value stays what it was here.
                    if (keys.Count == 0)
                        value.Effect.Set(value.Param.Key, now);
                    value.Effect.SetKeys(value.Param.Key, keys);
                }

                Log(at >= 0 ? $"Removed a keyframe of {value.Label} ({layer.Name})" : $"Keyed {value.Label} at {TimeDisplay.Format(PositionMs / 1000)} ({layer.Name})");
                break;
            }

            default:
                if (HasKeyframeAtPlayhead(layer))
                    RemoveKeyframe(layer);
                else
                    AddKeyframe(layer);
                break;
        }

        RefreshSelectedKey();
    }

    /// <summary>A diamond let go elsewhere on its row: the keyframe (on All, every motion keyframe at that moment) moves there.</summary>
    /// <param name="from">Where it was, in seconds of the sequence.</param>
    /// <param name="to">Where it was let go, in seconds of the sequence.</param>
    public void MoveLaneKey(Layer layer, object? lane, double from, double to)
    {
        var (start, end) = (from - layer.StartTime, Math.Max(to - layer.StartTime, 0));
        Checkpoint($"move a keyframe of {layer.Name}");
        bool moved;
        switch (lane)
        {
            case KeyProperty property:
                moved = layer.MoveKeys(start, end, property);
                break;

            case EffectKeyRef value:
            {
                var keys = value.Effect.GetKeys(value.Param.Key).ToList();
                var at = keys.FindIndex(k => Math.Abs(k.Time - start) <= Layer.KeyTolerance);
                moved = at >= 0;
                if (moved)
                {
                    var key = keys[at] with { Time = Math.Round(end, 3) };
                    keys.RemoveAt(at);
                    keys.RemoveAll(k => Math.Abs(k.Time - end) <= Layer.KeyTolerance);
                    keys.Add(key);
                    using (Layer.Echo())
                        value.Effect.SetKeys(value.Param.Key, keys);
                }

                break;
            }

            default:
                moved = layer.MoveKeys(start, end);
                break;
        }

        if (moved)
            Log($"Moved a {LaneName(lane)} keyframe of {layer.Name} to {TimeDisplay.Format(to)}");
        ApplyKeyframes();
        RefreshSelectedKey();
    }

    /// <summary>Delete in the Keyframes pane: the selected row's keyframe at the playhead goes.</summary>
    public void DeleteLaneKey(Layer layer, object? lane)
    {
        if (KeyOnLane(layer, lane) is null)
            return;

        if (lane is null)
        {
            RemoveKeyframe(layer);
        }
        else
        {
            ToggleLaneKey(layer, lane);
            ApplyKeyframes();
        }
    }

    /// <summary>A dot of the layout pane's motion path dragged: the place keyframes at that moment move by so much (fractions of the frame).</summary>
    public void MoveMotionKey(Layer clip, double time, double dx, double dy)
    {
        var (x, y) = (clip.GetKeyAt(time, KeyProperty.X), clip.GetKeyAt(time, KeyProperty.Y));
        if (x is null && y is null)
            return;

        Checkpoint($"move a keyframe of {clip.Name} on the frame");
        if (x is { } atX)
            clip.SetKey(KeyProperty.X, atX.Time, Math.Clamp(atX.Value + dx, Layer.SmallestPosition / 100, Layer.LargestPosition / 100));
        if (y is { } atY)
            clip.SetKey(KeyProperty.Y, atY.Time, Math.Clamp(atY.Value + dy, Layer.SmallestPosition / 100, Layer.LargestPosition / 100));
        ApplyKeyframes();
        Log($"Moved the keyframe at {TimeDisplay.Format(clip.StartTime + time)} of {clip.Name} on the frame");
    }

    /// <summary>What a row is called in the log and the undo list.</summary>
    public static string LaneName(object? lane) => lane switch
    {
        KeyProperty.X => "X",
        KeyProperty.Y => "Y",
        KeyProperty.Scale => "Scale",
        KeyProperty.Rotation => "Rotation",
        KeyProperty.Opacity => "Opacity",
        EffectKeyRef value => value.Label,
        _ => "motion",
    };

    /// <summary>An effect value at a moment of the clip: its keyframes', or its set value (a value of the layer's own, the layer's).</summary>
    public static double EffectValueAt(Layer layer, EffectKeyRef value, double time)
    {
        var keys = value.Effect.GetKeys(value.Param.Key);
        if (keys.Count > 0)
            return EasingCurves.ValueAt(keys, time);
        if (value.Param.Bind is { } bind && typeof(Layer).GetProperty(bind)?.GetValue(layer) is { } bound)
            return Convert.ToDouble(bound, System.Globalization.CultureInfo.InvariantCulture);
        return value.Effect.Get(value.Param.Key);
    }

    // ----- The easing of the selected keyframe, and a Custom Bezier's handles -----

    /// <summary>The curves the Custom Bezier boxes offer to start from.</summary>
    public IReadOnlyList<BezierPreset> BezierPresets => BezierPreset.All;

    private (double X1, double Y1, double X2, double Y2) _selectedHandles = EasingCurves.DefaultHandles;

    /// <summary>Whether the selected keyframe leaves by a Custom Bezier: its handles' boxes are shown.</summary>
    public bool IsBezierSelected => _selectedKey.Has && _selectedKey.Easing == EasingType.CubicBezier;

    public double BezierX1 { get => _selectedHandles.X1; set => SetHandles(_selectedHandles with { X1 = Math.Clamp(value, 0, 1) }); }
    public double BezierY1 { get => _selectedHandles.Y1; set => SetHandles(_selectedHandles with { Y1 = Math.Clamp(value, -2, 3) }); }
    public double BezierX2 { get => _selectedHandles.X2; set => SetHandles(_selectedHandles with { X2 = Math.Clamp(value, 0, 1) }); }
    public double BezierY2 { get => _selectedHandles.Y2; set => SetHandles(_selectedHandles with { Y2 = Math.Clamp(value, -2, 3) }); }

    /// <summary>A preset chosen in the Custom Bezier box: its handles go on the selected keyframe.</summary>
    public BezierPreset? SelectedBezierPreset
    {
        get => BezierPreset.All.FirstOrDefault(p => Math.Abs(p.X1 - _selectedHandles.X1) < 1e-3 && Math.Abs(p.Y1 - _selectedHandles.Y1) < 1e-3
            && Math.Abs(p.X2 - _selectedHandles.X2) < 1e-3 && Math.Abs(p.Y2 - _selectedHandles.Y2) < 1e-3);
        set
        {
            if (value is not null)
                SetHandles((value.X1, value.Y1, value.X2, value.Y2), whole: true);
        }
    }

    // A box's value dragged is one undo step: a checkpoint when the first change comes after a pause of a second.
    private long _handlesWrittenAt;

    private void SetHandles((double X1, double Y1, double X2, double Y2) handles, bool whole = false)
    {
        if (!IsBezierSelected || handles == _selectedHandles)
            return;

        var layer = ActiveKeyLayer;
        if (whole || Environment.TickCount64 - _handlesWrittenAt > 1000)
            Checkpoint($"change the curve of a keyframe of {layer.Name}");
        _handlesWrittenAt = Environment.TickCount64;
        SetLaneEasing(layer, EasingType.CubicBezier, handles);
    }

    /// <summary>Gives the keyframe at the playhead on the selected row an easing (with a Bezier's handles).</summary>
    private bool SetLaneEasing(Layer layer, EasingType easing, (double X1, double Y1, double X2, double Y2)? handles)
    {
        var time = ClipClock(layer);
        bool changed;
        if (SelectedKeyLane is EffectKeyRef value)
        {
            var keys = value.Effect.GetKeys(value.Param.Key).ToList();
            var at = keys.FindIndex(k => Math.Abs(k.Time - time) <= Layer.KeyTolerance);
            var eased = at >= 0 ? keys[at].WithEasing(easing, easing == EasingType.CubicBezier ? handles ?? EasingCurves.Handles(keys[at]) : null) : default;
            changed = at >= 0 && eased != keys[at];
            if (changed)
            {
                keys[at] = eased;
                using (Layer.Echo())
                    value.Effect.SetKeys(value.Param.Key, keys);
            }
        }
        else
        {
            changed = layer.SetEasingAt(time, easing, handles, SelectedKeyLane as KeyProperty?);
        }

        RefreshSelectedKey(force: true);
        return changed;
    }
}
