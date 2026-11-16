namespace HandPegApp.ViewModels;

/// <summary>
/// Change breadth, ordered (merges take the widest). Published as <see cref="MainViewModel.LastChange"/> with every
/// <see cref="MainViewModel.TimelineChanged"/> and <see cref="MainViewModel.PreviewStateInvalidated"/> so listeners rebuild only what
/// can be affected.
/// </summary>
public enum PreviewChange
{
    /// <summary>Pixels/audio only: effect parameter values, text or keyframes (not switch or range). Rebuilds the scene plan and audio
    /// program; Timeline rows, Projected Output and proxies are untouched.</summary>
    Look,

    /// <summary>Clip change visible on its Timeline row (keys, name, switches, card add/remove/toggle/range).</summary>
    Rows,

    /// <summary>Everything else (edits, settings, document swap): full rebuild.</summary>
    Output,
}

/// <summary>
/// Look-only invalidation (<see cref="PreviewChange.Look"/>) and look-write batching: 30 Hz Color Studio/Effects drags write several
/// values per flush (wheel R, G, B), each otherwise fanning out TimelineChanged and a full GenerateCommand. Inside
/// <see cref="BatchLookWrites"/> those coalesce into one announcement with the widest change when the outermost batch ends. Keyframes
/// still record per write (unlike <c>DeferCommand</c>, where RecordKey refuses).
/// </summary>
public partial class MainViewModel
{
    private readonly LookWriteBatch _lookWrites = new();

    /// <summary>Breadth of the current announcement; <see cref="PreviewChange.Output"/> outside look-only announcements.</summary>
    public PreviewChange LastChange => _lookWrites.Announcing;

    /// <summary>Opens a look-write batch; TimelineChanged and command regeneration run once when the outermost batch ends.</summary>
    internal LookWriteBatch.Scope BatchLookWrites()
    {
        _lookWrites.Depth++;
        return new LookWriteBatch.Scope(this);
    }

    /// <summary>Raises <see cref="TimelineChanged"/> with <paramref name="change"/> exposed as <see cref="LastChange"/> during dispatch.</summary>
    private void RaiseTimelineChanged(PreviewChange change = PreviewChange.Output)
    {
        var outer = _lookWrites.Announcing;
        _lookWrites.Announcing = change;
        try
        {
            TimelineChanged?.Invoke();
        }
        finally
        {
            _lookWrites.Announcing = outer;
        }
    }

    // Announces a layer change (OnLayerChanged) now or defers to the open batch; true when deferred.
    private bool DeferToLookBatch(PreviewChange change, bool command)
    {
        if (_lookWrites.Depth == 0)
            return false;
        _lookWrites.Pending = _lookWrites.Pending is { } pending && pending > change ? pending : change;
        _lookWrites.CommandPending |= command;
        return true;
    }

    /// <summary>Open look-write batch state (grouped object instead of loose VM fields).</summary>
    internal sealed class LookWriteBatch
    {
        public int Depth;
        public PreviewChange? Pending;
        public bool CommandPending;
        public PreviewChange Announcing = PreviewChange.Output;

        /// <summary>Ends a batch; the outermost flushes deferred work once.</summary>
        internal readonly struct Scope(MainViewModel owner) : IDisposable
        {
            public void Dispose()
            {
                var batch = owner._lookWrites;
                if (--batch.Depth > 0 || batch.Pending is not { } change)
                    return;
                var command = batch.CommandPending;
                (batch.Pending, batch.CommandPending) = (null, false);
                owner.RaiseTimelineChanged(change);
                if (command)
                    owner.GenerateCommand(change);
            }
        }
    }
}
