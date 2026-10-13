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

    ILanguageHost Create(ILanguageHostOptions? options = null);
}

public interface ILanguageHostRegistry
{
    IReadOnlyList<string> SupportedLanguages { get; }

    void Register(ILanguageHostFactory factory);
    void Unregister(string language);
    bool IsRegistered(string language);

    LanguageHostCapabilities GetCapabilities(string language);
    IReadOnlyDictionary<string, string> GetSnippets(string language);
    EditorLanguageProfile GetEditorProfile(string language);
    string GetDisplayName(string language);
    ILanguageHost Create(string language, ILanguageHostOptions? options = null);
}
