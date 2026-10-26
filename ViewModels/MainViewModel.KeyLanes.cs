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
        Checkpoint($"move a keyframe of {layer.Name}");
        if (MoveOneLaneKey(layer, lane, from - layer.StartTime, Math.Max(to - layer.StartTime, 0)))
            Log($"Moved a {LaneName(lane)} keyframe of {layer.Name} to {TimeDisplay.Format(to)}");
        ApplyKeyframes();
        RefreshSelectedKey();
    }

    /// <summary>Moves one keyframe of a row from one moment of the clip to another; false when there was none there.</summary>
    private static bool MoveOneLaneKey(Layer layer, object? lane, double start, double end)
    {
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

        return moved;
    }

    // ----- Several keyframes at once (4.1.1-B): the Keyframes pane's selection -----

    /// <summary>The keyframes selected in the Keyframes pane: each a row (as <see cref="SelectedKeyLane"/>) and its moment in seconds of the sequence.</summary>
    public IReadOnlyList<(object? Lane, double Time)> SelectedLaneKeys { get; set; } = [];

    // Copied keyframes: each its row, how long after the first one copied it is, and the keyframe itself (value and easing).
    private static List<(object? Lane, double Offset, Keyframe Key)>? _copiedKeys;

    public bool HasCopiedKeys => _copiedKeys is { Count: > 0 };

    /// <summary>Selected keyframes dragged together: each moves by the same time, as one undo step.</summary>
    public void MoveLaneKeys(Layer layer, IReadOnlyList<(object? Lane, double Time)> keys, double delta)
    {
        if (keys.Count == 0 || Math.Abs(delta) < 0.0005)
            return;

        Checkpoint($"move {keys.Count} keyframes of {layer.Name}");

        // The ones furthest along the way first, so that none is moved onto another that has yet to move.
        var moved = 0;
        foreach (var (lane, time) in delta > 0 ? keys.OrderByDescending(k => k.Time) : keys.OrderBy(k => k.Time))
        {
            var start = time - layer.StartTime;
            if (MoveOneLaneKey(layer, lane, start, Math.Max(start + delta, 0)))
                moved++;
        }

        if (moved > 0)
            Log($"Moved {moved} keyframes of {layer.Name} by {(delta >= 0 ? "+" : "-")}{TimeDisplay.Format(Math.Abs(delta))}");
        ApplyKeyframes();
        RefreshSelectedKey();
    }

    /// <summary>Delete with keyframes selected: every one of them goes, as one undo step (on All, every motion keyframe at that moment).</summary>
    public void DeleteLaneKeys(Layer layer, IReadOnlyList<(object? Lane, double Time)> keys)
    {
        if (keys.Count == 0)
            return;

        Checkpoint($"delete {keys.Count} keyframe{(keys.Count == 1 ? "" : "s")} of {layer.Name}");
        var deleted = 0;
        foreach (var (lane, time) in keys)
        {
            var at = time - layer.StartTime;
            switch (lane)
            {
                case KeyProperty property:
                    deleted += layer.RemoveKey(property, at) ? 1 : 0;
                    break;

                case EffectKeyRef value:
                {
                    var list = value.Effect.GetKeys(value.Param.Key).ToList();
                    if (list.RemoveAll(k => Math.Abs(k.Time - at) <= Layer.KeyTolerance) == 0)
                        break;

                    using (Layer.Echo())
                    {
                        // The last keyframe gone, the value stays what it was there.
                        if (list.Count == 0)
                            value.Effect.Set(value.Param.Key, EffectValueAt(layer, value, at));
                        value.Effect.SetKeys(value.Param.Key, list);
                    }

                    deleted++;
                    break;
                }

                default:
                    if (layer.HasKeyAt(at))
                    {
                        layer.RemoveMasterKey(at);
                        deleted++;
                    }

                    break;
            }
        }

        if (deleted > 0)
            Log($"Deleted {deleted} keyframe{(deleted == 1 ? "" : "s")} of {layer.Name}");
        ApplyKeyframes();
        RefreshSelectedKey();
    }

    /// <summary>Ctrl+C in the Keyframes pane: the keyframes given (on All, every motion keyframe at that moment) are kept to paste.</summary>
    public void CopyLaneKeys(Layer layer, IReadOnlyList<(object? Lane, double Time)> keys)
    {
        var copied = new List<(object? Lane, double Time, Keyframe Key)>();
        foreach (var (lane, time) in keys)
        {
            var at = time - layer.StartTime;
            switch (lane)
            {
                case KeyProperty property when layer.GetKeyAt(at, property) is { } key:
                    copied.Add((property, time, key));
                    break;
                case EffectKeyRef value when value.Effect.GetKeys(value.Param.Key).FirstOrDefault(k => Math.Abs(k.Time - at) <= Layer.KeyTolerance) is var key && key != default:
                    copied.Add((value, time, key));
                    break;
                case null:
                    foreach (var property in Enum.GetValues<KeyProperty>())
                    {
                        if (layer.GetKeyAt(at, property) is { } motion && !copied.Any(c => Equals(c.Lane, property) && Math.Abs(c.Time - time) <= Layer.KeyTolerance))
                            copied.Add((property, time, motion));
                    }

                    break;
            }
        }

        if (copied.Count == 0)
        {
            StatusText = "No keyframes to copy: select some in the Keyframes pane (Ctrl+click, Shift+click, or drag a box round them).";
            return;
        }

        var first = copied.Min(c => c.Time);
        _copiedKeys = [.. copied.Select(c => (c.Lane, c.Time - first, c.Key))];
        StatusText = $"Copied {copied.Count} keyframe{(copied.Count == 1 ? "" : "s")}: Ctrl+V in the Keyframes pane puts them in at the playhead, on this clip or another.";
    }

    /// <summary>
    /// Ctrl+V in the Keyframes pane: the copied keyframes go in at the playhead, as far apart as they were, with their
    /// values and easing. On another clip an effect value's keyframes go to its effect of the same kind; when it has none
    /// they are left out.
    /// </summary>
    public void PasteLaneKeys(Layer layer)
    {
        if (_copiedKeys is not { Count: > 0 } copied || !HasSource)
            return;

        Checkpoint($"paste {copied.Count} keyframe{(copied.Count == 1 ? "" : "s")} onto {layer.Name}");
        var (playhead, pasted) = (Math.Max(ClipClock(layer), 0), 0);
        foreach (var (lane, offset, key) in copied)
        {
            var at = Math.Round(playhead + offset, 3);
            switch (lane)
            {
                case KeyProperty property:
                    layer.SetKey(property, at, key.Value);
                    layer.SetEasingAt(at, key.Easing, key.Easing == EasingType.CubicBezier ? EasingCurves.Handles(key) : null, property);
                    pasted++;
                    break;

                case EffectKeyRef value when (layer.Effects.Contains(value.Effect) ? value.Effect : layer.Effects.FirstOrDefault(e => e.Type == value.Effect.Type)) is { } effect:
                {
                    var list = effect.GetKeys(value.Param.Key).Where(k => Math.Abs(k.Time - at) > Layer.KeyTolerance).Append(key with { Time = at }).OrderBy(k => k.Time).ToList();
                    using (Layer.Echo())
                        effect.SetKeys(value.Param.Key, list);
                    pasted++;
                    break;
                }
            }
        }

        Log(pasted == copied.Count
            ? $"Pasted {pasted} keyframe{(pasted == 1 ? "" : "s")} at {TimeDisplay.Format(PositionMs / 1000)} ({layer.Name})"
            : $"Pasted {pasted} of {copied.Count} keyframes at {TimeDisplay.Format(PositionMs / 1000)} ({layer.Name}): it has no effect of the kind the others were on");
        ApplyKeyframes();
        RefreshSelectedKey();
    }

    /// <summary>The easing box with several keyframes selected: each of them leaves that way. False when none changed.</summary>
    private bool SetSelectedKeysEasing(Layer layer, EasingType easing)
    {
        var changed = false;
        foreach (var (lane, time) in SelectedLaneKeys)
        {
            var at = time - layer.StartTime;
            if (lane is EffectKeyRef value)
            {
                var keys = value.Effect.GetKeys(value.Param.Key).ToList();
                var index = keys.FindIndex(k => Math.Abs(k.Time - at) <= Layer.KeyTolerance);
                if (index < 0)
                    continue;

                var eased = keys[index].WithEasing(easing, easing == EasingType.CubicBezier ? EasingCurves.Handles(keys[index]) : null);
                if (eased == keys[index])
                    continue;

                keys[index] = eased;
                using (Layer.Echo())
                    value.Effect.SetKeys(value.Param.Key, keys);
                changed = true;
            }
            else
            {
                changed |= layer.SetEasingAt(at, easing, null, lane as KeyProperty?);
            }
        }

        RefreshSelectedKey(force: true);
        return changed;
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
