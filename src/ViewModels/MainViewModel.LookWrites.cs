namespace HandPegApp.ViewModels;

/// <summary>
/// How much a change touches, ordered by breadth (a merge of several takes the widest). Announced with every
/// <see cref="MainViewModel.TimelineChanged"/> and <see cref="MainViewModel.PreviewStateInvalidated"/> as
/// <see cref="MainViewModel.LastChange"/>, so each listener rebuilds only what the change can reach.
/// </summary>
public enum PreviewChange
{
    /// <summary>Pixels or sound only: an effect parameter's value, text or keyframes (not its switch or range). The scene plan and the
    /// audio program are rebuilt; the Timeline's rows, the Projected Output and the proxies are not (nothing they show can change).</summary>
    Look,

    /// <summary>A clip changed in a way its Timeline row can show (keys, name, switches, a card added, removed, switched or re-ranged).</summary>
    Rows,

    /// <summary>Anything else (an edit, a setting, a document put in place): everything is rebuilt.</summary>
    Output,
}

/// <summary>
/// Look-only invalidation (<see cref="PreviewChange.Look"/>) and look-write batches: a 30 Hz Color Studio or Effects drag writes several
/// values per flush (a colour wheel's R, G and B); each raised a TimelineChanged fan-out and a full GenerateCommand. Inside
/// <see cref="BatchLookWrites"/> those announcements wait and run once, with the widest change seen, when the outermost batch ends.
/// Keyframes are still recorded per write (unlike <c>DeferCommand</c>, under which RecordKey refuses to key).
/// </summary>
public partial class MainViewModel
{
    private readonly LookWriteBatch _lookWrites = new();

    /// <summary>The breadth of the change being announced now; <see cref="PreviewChange.Output"/> outside a look-only announcement.</summary>
    public PreviewChange LastChange => _lookWrites.Announcing;

    /// <summary>Opens a batch of look writes: TimelineChanged and the command wait for the outermost batch to end, then run once.</summary>
    internal LookWriteBatch.Scope BatchLookWrites()
    {
        _lookWrites.Depth++;
        return new LookWriteBatch.Scope(this);
    }

    /// <summary>Raises <see cref="TimelineChanged"/> announcing <paramref name="change"/> (<see cref="LastChange"/> while it runs).</summary>
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

    // A layer's change announced (OnLayerChanged): now, or at the end of the open batch. True when it waits for the batch.
    private bool DeferToLookBatch(PreviewChange change, bool command)
    {
        if (_lookWrites.Depth == 0)
            return false;
        _lookWrites.Pending = _lookWrites.Pending is { } pending && pending > change ? pending : change;
        _lookWrites.CommandPending |= command;
        return true;
    }

    /// <summary>The state of the open look-write batches (one object, not loose view-model fields).</summary>
    internal sealed class LookWriteBatch
    {
        public int Depth;
        public PreviewChange? Pending;
        public bool CommandPending;
        public PreviewChange Announcing = PreviewChange.Output;

        /// <summary>Ends a batch; the outermost runs what its writes owe, once.</summary>
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
