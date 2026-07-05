namespace DotPad.Contracts.IntelliSense;

public interface IIntelliSenseService : IDisposable
{
    Task<ICompletionList?> GetCompletionsAsync(
        string code,
        int position,
        CompletionTrigger trigger = default,
        CancellationToken cancellationToken = default);

    Task<ISignatureHelpResult?> GetSignatureHelpAsync(
        string code,
        int position,
        char? triggerCharacter = null,
        CancellationToken cancellationToken = default);

    Task<IQuickInfoResult?> GetQuickInfoAsync(
        string code,
        int position,
        CancellationToken cancellationToken = default);

    // Fires from a background thread. First arg = the code that produced the diagnostics.
    event Action<string, IReadOnlyList<IDiagnosticInfo>>? DiagnosticsChanged;

    // Fires from a background thread. First arg = the code that produced the tokens.
    event Action<string, IReadOnlyList<ISemanticToken>>? SemanticTokensChanged;

    // Fires from a background thread. First arg = the code the fixes were computed against.
    event Action<string, IReadOnlyList<ICodeFixInfo>>? CodeFixesChanged;

    void NotifyTextChanged(string code);

    // Call when the tab's NuGet packages change so IntelliSense sees the new types.
    void UpdatePackageReferences(IReadOnlyDictionary<string, string> packages);

    // Call when the tab's .rpad front-matter `usings:` list changes (including once at initial
    // load) so completion/diagnostics/quick-fix ranking see the same global usings the compiler
    // will add — see ICodeFixInfo.UsingsToAdd.
    void UpdateGlobalUsings(IReadOnlyList<string> usings);
}
