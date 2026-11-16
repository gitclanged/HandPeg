using System.Security.Cryptography;
using System.Text;

namespace HandPegApp.ViewModels;

/// <summary>
/// Tracks Export command parts supplied by a project file rather than this session: a saved hand-edited FFmpeg command and the
/// project's Extra options (appended to every generated command). Untrusted FFmpeg arguments can write anywhere or exfiltrate data,
/// so they run only after one explicit confirmation (<see cref="MainViewModel"/> export and Add to Queue). Locally modified text is
/// not prompted: the check compares the effective text with the file-supplied text.
/// <para>Confirmed or locally authored-and-saved text is trusted by fingerprint (<see cref="FingerprintOf"/>, stored in
/// AppSettings.TrustedProjectCommands, never in a project file), so it runs without prompting when a project brings it back.</para>
/// </summary>
internal sealed class CommandTrust
{
    private string? _command;
    private string? _extras;

    /// <summary>Trust fingerprint: kind prefix plus SHA-256 of the text.</summary>
    public static string FingerprintOf(bool extras, string text) =>
        (extras ? "x:" : "c:") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>On project open: records the saved command and Extra options (either may be empty) as file-supplied until confirmed.</summary>
    public void TookFromFile(string? command, string? extraArguments)
    {
        _command = string.IsNullOrWhiteSpace(command) ? null : command;
        _extras = string.IsNullOrWhiteSpace(extraArguments) ? null : extraArguments;
    }

    /// <summary>No file-supplied text in effect (new project, or a session-created queued job).</summary>
    public void Clear() => (_command, _extras) = (null, null);

    /// <summary>Whether <paramref name="command"/> with <paramref name="extraArguments"/> includes unconfirmed file-supplied text.</summary>
    public bool NeedsConfirmation(string command, string extraArguments, IReadOnlyCollection<string> trusted) =>
        IsFileCommand(command, trusted) || AreFileExtras(extraArguments, trusted);

    /// <summary>Whether the effective command is an untrusted file-supplied hand edit (background queues drop it).</summary>
    public bool IsFileCommand(string command, IReadOnlyCollection<string> trusted) =>
        _command is not null && string.Equals(command, _command, StringComparison.Ordinal) && !trusted.Contains(FingerprintOf(extras: false, command));

    /// <summary>Whether the effective Extra options are untrusted file-supplied text.</summary>
    public bool AreFileExtras(string extraArguments, IReadOnlyCollection<string> trusted) =>
        _extras is not null && string.Equals(extraArguments, _extras, StringComparison.Ordinal) && !trusted.Contains(FingerprintOf(extras: true, extraArguments));

    /// <summary>Marks file-supplied text as confirmed for this session.</summary>
    public void Confirmed() => Clear();
}
