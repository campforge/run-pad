namespace DotPad.Contracts.IntelliSense;

// TODO.md #62 "Go to Implementation" (Ctrl+F12, natural follow-up to Go to Definition/F12 from the
// eighty-seventh pass): unlike IDefinitionResult, this never carries external/metadata info — a
// single-file script's own AdhocWorkspace only ever contains the script itself, so
// SymbolFinder.FindImplementationsAsync/FindOverridesAsync can only ever find OTHER declarations
// inside that same document. Every result is therefore an in-document offset.
public interface IImplementationLocation
{
    int Offset { get; }

    /// <summary>Human-readable label for a picker menu when there's more than one implementation, e.g. "FileLogger.Write(string)".</summary>
    string DisplayName { get; }
}
