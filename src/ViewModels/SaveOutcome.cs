namespace HandPegApp.ViewModels;

/// <summary>Project save result.</summary>
public enum SaveResult
{
    /// <summary>Project written.</summary>
    Saved,

    /// <summary>Intentionally not written: another project's file was kept, or a prior write is still in progress.</summary>
    Declined,

    /// <summary>Write failed (read-only folder, disk full, or file newer than this version's format).</summary>
    Failed,
}

/// <summary>
/// Save result plus its status line. Callers branch on <see cref="Result"/>, never on the user-facing (localizable) wording.
/// </summary>
public readonly record struct SaveOutcome(SaveResult Result, string Line)
{
    /// <summary>Whether the project was saved.</summary>
    public bool IsSaved => Result == SaveResult.Saved;

    public override string ToString() => Line;
}
