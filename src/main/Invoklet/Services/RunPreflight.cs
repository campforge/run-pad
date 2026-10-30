namespace Invoklet.Services;

/// <summary>What a tab knows about its script before it may run.</summary>
/// <param name="DocumentParseError">Why the file is not a valid document; the editor then holds the whole file, not a script.</param>
/// <param name="FilePath">The script's file; null for an untitled script.</param>
/// <param name="IsSelection">Only the selected text runs, which is never saved first.</param>
internal sealed record RunPreflightInput(string? DocumentParseError, string? FilePath, bool IsSelection);

internal abstract record RunPreflightOutcome;

/// <summary>Everything is in order: the script may run.</summary>
internal sealed record RunMayProceed : RunPreflightOutcome;

/// <summary>The run does not start and there is nothing to tell: it was stopped meanwhile, or the user cancelled the save.</summary>
internal sealed record RunAborted : RunPreflightOutcome;

/// <summary>An invalid document is never run as raw code.</summary>
internal sealed record RunBlockedByInvalidDocument(string ParseError) : RunPreflightOutcome;

/// <summary>The script could not be saved, so it does not run (and the file is not left out of step with what ran).</summary>
internal sealed record RunBlockedBySaveFailure(string FileName, string Error) : RunPreflightOutcome;

/// <summary>A selected argument set the script no longer has: say so rather than run with other arguments.</summary>
internal sealed record RunBlockedByArguments(string Message) : RunPreflightOutcome;

/// <summary>
/// The checks before a run, in order: the document is valid; the script is saved like any save (a file changed
/// by another program is only replaced on the user's choice; an untitled script or a selection is not saved);
/// the argument set resolves. Nothing of the run has started when one of them stops it.
/// </summary>
internal sealed class RunPreflight
{
    /// <param name="save">Saves the script and says how it went; marks the tab as saving for as long as it takes.</param>
    /// <param name="mustAbort">Whether the tab was closed, restarted or stopped meanwhile; asked after the save.</param>
    /// <param name="argumentsError">Why the selected argument set cannot be resolved, if it cannot; asked last, after the save.</param>
    public async Task<RunPreflightOutcome> CheckAsync(
        RunPreflightInput input, Func<Task<InPlaceSaveResult>> save, Func<bool> mustAbort, Func<string?> argumentsError)
    {
        if (input.DocumentParseError is { } parseError) return new RunBlockedByInvalidDocument(parseError);

        if (!input.IsSelection && input.FilePath is { } path)
        {
            var saved = await save();
            if (mustAbort()) return new RunAborted();
            if (saved.Status == InPlaceSaveStatus.Failed) return new RunBlockedBySaveFailure(Path.GetFileName(path), saved.Error);
            if (saved.Status != InPlaceSaveStatus.Saved) return new RunAborted();
        }

        return argumentsError() is { } error ? new RunBlockedByArguments(error) : new RunMayProceed();
    }
}
