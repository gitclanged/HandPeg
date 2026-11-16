using System.Security.Cryptography;
using System.Text;

namespace HandPegApp.ViewModels;

/// <summary>
/// What of the Export command came from a project file rather than from this session: a hand-edited FFmpeg command saved in the
/// project, and the project's Extra options (appended to every generated command). Whoever made the file chose them, and even the
/// genuine FFmpeg given foreign arguments can write files anywhere or send data out, so they run only after the user has seen and
/// confirmed them once (<see cref="MainViewModel"/>'s export and Add to Queue). A command or Extra options the user has since
/// changed are their own and are not asked about: the check compares the text in effect with the text the file brought.
/// <para>Text the user confirmed, or wrote on this PC and saved, is remembered by its fingerprint (<see cref="FingerprintOf"/>, kept in
/// AppSettings.TrustedProjectCommands, never in a project file): a project bringing it back again runs it without asking.</para>
/// </summary>
internal sealed class CommandTrust
{
    private string? _command;
    private string? _extras;

    /// <summary>The fingerprint a command (or Extra options) is remembered by on this PC: its kind and the SHA-256 of its text.</summary>
    public static string FingerprintOf(bool extras, string text) =>
        (extras ? "x:" : "c:") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A project was opened: its saved command and Extra options (either may be empty) are the file's until confirmed.</summary>
    public void TookFromFile(string? command, string? extraArguments)
    {
        _command = string.IsNullOrWhiteSpace(command) ? null : command;
        _extras = string.IsNullOrWhiteSpace(extraArguments) ? null : extraArguments;
    }

    /// <summary>Nothing in effect came from a file (a new project, this session's own queued job).</summary>
    public void Clear() => (_command, _extras) = (null, null);

    /// <summary>Whether running <paramref name="command"/> with <paramref name="extraArguments"/> runs something only the file chose.</summary>
    public bool NeedsConfirmation(string command, string extraArguments, IReadOnlyCollection<string> trusted) =>
        IsFileCommand(command, trusted) || AreFileExtras(extraArguments, trusted);

    /// <summary>Whether the command in effect is the file's own hand-edited command, not trusted here (a background queue drops it).</summary>
    public bool IsFileCommand(string command, IReadOnlyCollection<string> trusted) =>
        _command is not null && string.Equals(command, _command, StringComparison.Ordinal) && !trusted.Contains(FingerprintOf(extras: false, command));

    /// <summary>Whether the Extra options in effect are the file's own, not trusted here.</summary>
    public bool AreFileExtras(string extraArguments, IReadOnlyCollection<string> trusted) =>
        _extras is not null && string.Equals(extraArguments, _extras, StringComparison.Ordinal) && !trusted.Contains(FingerprintOf(extras: true, extraArguments));

    /// <summary>The user confirmed: from now on they are this session's.</summary>
    public void Confirmed() => Clear();
}
