using HandPegApp.Services;

namespace HandPegApp.ViewModels;

// ----- A project's own command, confirmed before it runs (CommandTrust) -----

public sealed partial class MainViewModel
{
    private readonly CommandTrust _commandTrust = new();

    /// <summary>Fingerprints of project commands trusted on this PC (AppSettings.TrustedProjectCommands).</summary>
    private static IReadOnlyCollection<string> TrustedCommands => AppSettings.Current.TrustedProjectCommands;

    /// <summary>
    /// Confirms once before running a command or Extra options supplied by the opened project file (not session edits or already-trusted
    /// text). Refused when headless (no Confirm hook). Confirmed text is trusted on this PC.
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
    /// On save, trusts locally authored command/Extra options (not unconfirmed file-supplied text) so reopening one's own project
    /// never prompts.
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

    /// <summary>Trusts a command and Extra options (either may be empty) on this PC; bounded list, oldest evicted.</summary>
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
    /// Headless restore (background job while encoding): drops untrusted file-supplied command/Extra options and regenerates the command.
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
