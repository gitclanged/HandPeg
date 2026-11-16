namespace HandPegApp.ViewModels;

/// <summary>What a project save did.</summary>
public enum SaveResult
{
    /// <summary>The project was written.</summary>
    Saved,

    /// <summary>Nothing was written by choice: the user kept another project's file, or the save was not possible yet (a write still under way).</summary>
    Declined,

    /// <summary>Writing failed (the folder is read-only, the disk is full, the file is newer than this version writes).</summary>
    Failed,
}

/// <summary>
/// A project save's result and the status line it showed. Callers branch on <see cref="Result"/>, never on the line's wording,
/// which is user-facing text free to change (and to be localized).
/// </summary>
public readonly record struct SaveOutcome(SaveResult Result, string Line)
{
    /// <summary>Whether the project was written.</summary>
    public bool IsSaved => Result == SaveResult.Saved;

    public override string ToString() => Line;
}
