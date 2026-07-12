namespace DotPad.Contracts;

// Marker interface — extend with language-specific settings.
public interface ILanguageHostOptions { }

public interface ILanguageHostFactory
{
    string Language { get; }
    LanguageHostCapabilities Capabilities { get; }

    // Tab-expandable editor snippets for this language: keyword → template with a `${0}` cursor
    // marker (see DotPad.Avalonia.CodeTextEditor.CodeTextEditor.Snippets). Empty for languages with
    // none defined yet.
    IReadOnlyDictionary<string, string> Snippets { get; }

    // Lexical/syntactic conventions for CodeTextEditor's triggers, brace pairs, and indentation —
    // see EditorLanguageProfile. Same reasoning as Snippets above (TODO.md #58).
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
    ILanguageHost Create(string language, ILanguageHostOptions? options = null);
}
