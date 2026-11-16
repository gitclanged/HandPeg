using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>Keyframes pane row reference to one animated effect card value.</summary>
public sealed record EffectKeyRef(EffectInstance Effect, EffectParam Param, string Label);

// Keyframes pane rows (KeyLanesView): All (every motion property, as Key All), one per motion property, one per animated effect
// value. Rows key, move and delete independently; the easing box edits the selected row. Row identity: KeyProperty, EffectKeyRef,
// or null for All.
public partial class MainViewModel
{
    private object? _selectedKeyLane;

    /// <summary>Selected Keyframes row: null (All), KeyProperty or EffectKeyRef.</summary>
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

    /// <summary>Playhead in clip-local time (keyframe time base).</summary>
    private double ClipClock(Layer layer) => PositionMs / 1000 - layer.StartTime;

    /// <summary>Keyframe at the playhead on a row; null when none.</summary>
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

    /// <summary>Row diamond toggle: adds a keyframe at the playhead with the current value, or removes the existing one.</summary>
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
                var at = KeyEdit.IndexAt(keys, time);
                var now = EffectValueAt(layer, value, time);
                Checkpoint($"key {value.Label} of {layer.Name}");
                if (at >= 0)
                    keys.RemoveAt(at);
                else
                    keys.Add(new Keyframe(Math.Round(time, 3), now));

                using (OwnLookScope())
                {
                    // Removing the last keyframe keeps the value at this time as the static value.
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

    /// <summary>Diamond drag release: moves the keyframe (on All, every motion keyframe at that time).</summary>
    /// <param name="from">Original time, Sequence seconds.</param>
    /// <param name="to">Drop time, Sequence seconds.</param>
    public void MoveLaneKey(Layer layer, object? lane, double from, double to)
    {
        Checkpoint($"move a keyframe of {layer.Name}");
        if (MoveOneLaneKey(layer, lane, from - layer.StartTime, Math.Max(to - layer.StartTime, 0)))
            Log($"Moved a {LaneName(lane)} keyframe of {layer.Name} to {TimeDisplay.Format(to)}");
        ApplyKeyframes();
        RefreshSelectedKey();
    }

    /// <summary>Moves a row's keyframe between clip-local times; false when none at the source.</summary>
    private bool MoveOneLaneKey(Layer layer, object? lane, double start, double end)
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
                    using (OwnLookScope())
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

    // ----- Keyframes pane multi-selection -----

    /// <summary>Selected keyframes as (row like <see cref="SelectedKeyLane"/>, Sequence seconds).</summary>
    public IReadOnlyList<(object? Lane, double Time)> SelectedLaneKeys { get; set; } = [];

    // Copied keyframes: row, offset from the first copied, and the keyframe (value, easing).
    private static List<(object? Lane, double Offset, Keyframe Key)>? _copiedKeys;

    public bool HasCopiedKeys => _copiedKeys is { Count: > 0 };

    /// <summary>Moves all selected keyframes by the same delta in one undo step.</summary>
    public void MoveLaneKeys(Layer layer, IReadOnlyList<(object? Lane, double Time)> keys, double delta)
    {
        if (keys.Count == 0 || Math.Abs(delta) < 0.0005)
            return;

        Checkpoint($"move {keys.Count} keyframes of {layer.Name}");

        // Process in move direction, furthest first, so no key lands on one not yet moved.
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

    /// <summary>Deletes all selected keyframes in one undo step (on All, every motion keyframe at that time).</summary>
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

                    using (OwnLookScope())
                    {
                        // Removing the last keyframe keeps the value at that time as the static value.
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

    /// <summary>Keyframes pane Ctrl+C: copies the given keyframes (on All, every motion keyframe at that time).</summary>
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
    /// Keyframes pane Ctrl+V: pastes at the playhead preserving spacing, values and easing. On another clip, effect keyframes map to
    /// the same effect kind and are skipped if absent.
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
                    using (OwnLookScope())
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

    /// <summary>Applies an outgoing easing to all selected keyframes; false when none changed.</summary>
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
                using (OwnLookScope())
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

    /// <summary>Keyframes pane Delete: removes the selected row's keyframe at the playhead.</summary>
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

    /// <summary>Motion path dot drag: offsets position keyframes at that time (frame fractions).</summary>
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

    /// <summary>Row label for the log and undo list.</summary>
    public static string LaneName(object? lane) => lane switch
    {
        KeyProperty.X => "X",
        KeyProperty.Y => "Y",
        KeyProperty.Scale => "Scale",
        KeyProperty.Rotation => "Rotation",
        KeyProperty.Opacity => "Opacity",
        KeyProperty.TiltX => "Tilt X (Pitch)",
        KeyProperty.TiltY => "Tilt Y (Yaw)",
        EffectKeyRef value => value.Label,
        _ => "motion",
    };

    /// <summary>Effect value at a clip time: interpolated keyframes, else its static value (layer-owned values read from the layer).</summary>
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

    /// <summary>Custom Bezier starting presets.</summary>
    public IReadOnlyList<BezierPreset> BezierPresets => BezierPreset.All;

    private (double X1, double Y1, double X2, double Y2) _selectedHandles = EasingCurves.DefaultHandles;

    /// <summary>Selected keyframe uses Custom Bezier (shows handle boxes).</summary>
    public bool IsBezierSelected => _selectedKey.Has && _selectedKey.Easing == EasingType.CubicBezier;

    public double BezierX1 { get => _selectedHandles.X1; set => SetHandles(_selectedHandles with { X1 = Math.Clamp(value, 0, 1) }); }
    public double BezierY1 { get => _selectedHandles.Y1; set => SetHandles(_selectedHandles with { Y1 = Math.Clamp(value, -2, 3) }); }
    public double BezierX2 { get => _selectedHandles.X2; set => SetHandles(_selectedHandles with { X2 = Math.Clamp(value, 0, 1) }); }
    public double BezierY2 { get => _selectedHandles.Y2; set => SetHandles(_selectedHandles with { Y2 = Math.Clamp(value, -2, 3) }); }

    /// <summary>Applies a Custom Bezier preset's handles to the selected keyframe.</summary>
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

    // A box drag is one undo step: checkpoint on the first change after a 1 s pause.
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

    /// <summary>Sets easing (with Bezier handles) on the selected row's keyframe at the playhead.</summary>
    private bool SetLaneEasing(Layer layer, EasingType easing, (double X1, double Y1, double X2, double Y2)? handles)
    {
        var time = ClipClock(layer);
        bool changed;
        if (SelectedKeyLane is EffectKeyRef value)
        {
            var keys = value.Effect.GetKeys(value.Param.Key).ToList();
            var at = KeyEdit.IndexAt(keys, time);
            var eased = at >= 0 ? keys[at].WithEasing(easing, easing == EasingType.CubicBezier ? handles ?? EasingCurves.Handles(keys[at]) : null) : default;
            changed = at >= 0 && eased != keys[at];
            if (changed)
            {
                keys[at] = eased;
                using (OwnLookScope())
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
