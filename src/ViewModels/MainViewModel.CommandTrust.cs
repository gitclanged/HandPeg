using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- A project's own command, confirmed before it runs (CommandTrust) -----

public sealed partial class MainViewModel
{
    private readonly CommandTrust _commandTrust = new();

    /// <summary>The fingerprints of the project commands trusted on this PC (AppSettings.TrustedProjectCommands).</summary>
    private static IReadOnlyCollection<string> TrustedCommands => AppSettings.Current.TrustedProjectCommands;

    /// <summary>
    /// Asks once before running a command, or Extra options, that the opened project file carried (not this session's edits, nor text
    /// trusted on this PC). With no one to ask (no Confirm hook) it is refused. Confirmed text is remembered on this PC.
    /// </summary>
    /// <returns>Whether the command may run.</returns>
    private bool ConfirmFileCommand(string command)
    {
        if (!_commandTrust.NeedsConfirmation(command, ExtraVideoArguments, TrustedCommands))
            return true;

        var what = _commandTrust.IsFileCommand(command, TrustedCommands) ? "its own FFmpeg command" : $"its own FFmpeg options ({ExtraVideoArguments})";
        if (Confirm?.Invoke("Project Command", $"This project carries {what}, saved with the file:\n\n{command}\n\nRun it?", "Run") != true)
            return false;

        TrustOnThisPc(IsCommandManuallyEdited ? command : null, ExtraVideoArguments);
        _commandTrust.Confirmed();
        return true;
    }

    /// <summary>
    /// A project being saved: a hand-edited command and Extra options it carries that are this PC's (not a file's still unconfirmed) are
    /// remembered as trusted, so opening the user's own project again never asks.
    /// </summary>
    private void TrustOwnCommandOnSave()
    {
        if (_isBackgroundWorker)
            return;
        var command = IsCommandManuallyEdited ? CommandPreview : null;
        var ownCommand = command is { Length: > 0 } && !_commandTrust.IsFileCommand(command, TrustedCommands) ? command : null;
        var ownExtras = ExtraVideoArguments is { Length: > 0 } extras && !_commandTrust.AreFileExtras(extras, TrustedCommands) ? extras : null;
        TrustOnThisPc(ownCommand, ownExtras);
    }

    /// <summary>Remembers a command and Extra options (either may be null or empty) as trusted on this PC, newest last, the oldest dropped.</summary>
    private static void TrustOnThisPc(string? command, string? extras)
    {
        var wanted = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(command))
            wanted.Add(CommandTrust.FingerprintOf(extras: false, command));
        if (!string.IsNullOrWhiteSpace(extras))
            wanted.Add(CommandTrust.FingerprintOf(extras: true, extras));
        if (wanted.Count == 0 || wanted.All(TrustedCommands.Contains))
            return;

        AppSettings.Update(settings =>
        {
            var list = settings.TrustedProjectCommands;
            list.RemoveAll(wanted.Contains);
            list.AddRange(wanted);
            if (list.Count > AppSettings.TrustedProjectCommandsKept)
                list.RemoveRange(0, list.Count - AppSettings.TrustedProjectCommandsKept);
        });
    }

    /// <summary>
    /// A project restored with no one to confirm its command (a background job made while the window encodes): the file's own command
    /// and Extra options are dropped (unless trusted on this PC) and the command generated again.
    /// </summary>
    /// <returns>Whether anything was dropped.</returns>
    private bool DropFileCommand()
    {
        var dropped = false;
        if (_commandTrust.AreFileExtras(ExtraVideoArguments, TrustedCommands))
        {
            ExtraVideoArguments = "";
            dropped = true;
        }

        if (IsCommandManuallyEdited && _commandTrust.IsFileCommand(CommandPreview, TrustedCommands))
        {
            IsCommandManuallyEdited = false;
            dropped = true;
        }

        if (dropped)
        {
            GenerateCommand();
            _commandTrust.Clear();
        }

        return dropped;
    }
}
