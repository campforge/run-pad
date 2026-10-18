using RunPad.Contracts.IntelliSense;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;

namespace RunPad.Contracts;

// Marker interface — extend with language-specific settings.
public interface ILanguageHostOptions { }

// Shared default for every concrete ILanguageHostOptions' own OutputName property (the base name a
// language's compiler writes its throwaway source/project/binary under, inside a script's temp or
// persistent build-cache output directory) — a leftover generic identifier from the project's
// pre-RunPad name, kept only as an internal build-artifact name (never shown to the user), not
// worth renaming and touching every persisted build-cache directory's on-disk file names. Was
// previously the same string literal duplicated independently across both language options classes
// and EditorTabItemControl's own sourceFilePath computation.
public static class LanguageHostOptionsDefaults
{
    public const string OutputName = "RunPadQuery";
}

public interface ILanguageHostFactory
{
    string Language { get; }

    // Human-readable name for UI surfaces that list languages (e.g. the Preferences window's
    // Languages settings tree) — Language itself is the internal registry key
    // ("csharp"/"vb"), not something to show a user directly.
    string DisplayName { get; }

    LanguageHostCapabilities Capabilities { get; }

    // Tab-expandable editor snippets for this language: keyword → template with a `${0}` cursor
    // marker (see RunPad.Avalonia.CodeTextEditor.CodeTextEditor.Snippets). Empty for languages with
    // none defined yet.
    IReadOnlyDictionary<string, string> Snippets { get; }

    // Lexical/syntactic conventions for CodeTextEditor's triggers, brace pairs, and indentation —
    // see EditorLanguageProfile. Same reasoning as Snippets above.
    EditorLanguageProfile EditorProfile { get; }

    // The code a script gets when it switches to <kind> — for Program, a Main skeleton for empty code
    // or the existing statements wrapped in Main — with a `${0}` caret marker, as in Snippets. Null
    // when the code should stay as it is (it already fits the kind, or the language has no template).
    string? CodeForKind(string code, RpadKind kind) => null;

    // Editor intelligence (completion, diagnostics, quick fixes...) for one open document, or null when
    // the language has none. Every call creates a new, stateful service that the caller owns and
    // disposes: one per tab, never shared. Creating it can take a while (it loads metadata), so callers
    // run it off the UI thread. A cancelled token may end it early with OperationCanceledException;
    // a service that is returned anyway still belongs to the caller.
    IIntelliSenseService? CreateIntelliSense(CancellationToken cancellationToken) => null;

    // Prepares what every CreateIntelliSense call shares, so the first document opens fast. Off the UI thread.
    void WarmUpIntelliSense() { }

    ILanguageHost Create(ILanguageHostOptions? options = null);

    // A host for what the run asks for (see ExecutionRequest): the language's backend builds its own
    // options from it — references, macros, package sources, its runtime library — so callers never do.
    ILanguageHost Create(ExecutionRequest request)
        => throw new NotSupportedException($"The {DisplayName} language does not run execution requests.");
}

/// <summary>A registered language as the app offers it.</summary>
/// <param name="Id">The registry key, also the <c>language:</c> value of a .rpad file.</param>
/// <param name="DisplayName">The name shown for it, such as "Visual Basic".</param>
/// <param name="IconResourceKey">Its icon's resource key (see <see cref="EditorLanguageProfile"/>), or null.</param>
public sealed record LanguageDescriptor(
    string Id, string DisplayName, LanguageHostCapabilities Capabilities, string? IconResourceKey);

public interface ILanguageHostRegistry
{
    IReadOnlyList<string> SupportedLanguages { get; }

    // In registration order: the order the app lists them in.
    IReadOnlyList<LanguageDescriptor> Languages { get; }

    void Register(ILanguageHostFactory factory);
    void Unregister(string language);
    bool IsRegistered(string language);

    LanguageHostCapabilities GetCapabilities(string language);
    IReadOnlyDictionary<string, string> GetSnippets(string language);
    EditorLanguageProfile GetEditorProfile(string language);
    string GetDisplayName(string language);
    string? GetCodeForKind(string language, string code, RpadKind kind);

    // See ILanguageHostFactory.CreateIntelliSense; null for a language that isn't registered.
    IIntelliSenseService? CreateIntelliSense(string language, CancellationToken cancellationToken);

    // Every registered language's ILanguageHostFactory.WarmUpIntelliSense.
    void WarmUpIntelliSense();

    ILanguageHost Create(string language, ILanguageHostOptions? options = null);

    // See ILanguageHostFactory.Create(ExecutionRequest).
    ILanguageHost Create(string language, ExecutionRequest request);
}
