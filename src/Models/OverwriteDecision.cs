namespace HandPegApp.Models;

public enum OverwriteChoice
{
    Overwrite,
    Rename,
    Cancel,
}

/// <summary>What the user chose when the target file already exists.</summary>
/// <param name="NewPath">The path to write to instead; only meaningful for <see cref="OverwriteChoice.Rename"/>.</param>
public sealed record OverwriteDecision(OverwriteChoice Choice, string NewPath = "");
