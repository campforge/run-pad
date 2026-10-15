using RunPad.Format.RPad;

namespace RunPad.Contracts;

public sealed class LanguageHostRegistry : ILanguageHostRegistry
{
    private readonly Dictionary<string, ILanguageHostFactory> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> SupportedLanguages => _factories.Keys.ToList();

    public void Register(ILanguageHostFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[factory.Language] = factory;
    }

    public void Unregister(string language) => _factories.Remove(language);

    public bool IsRegistered(string language) => _factories.ContainsKey(language);

    public LanguageHostCapabilities GetCapabilities(string language) =>
        GetFactory(language).Capabilities;

    public IReadOnlyDictionary<string, string> GetSnippets(string language) =>
        GetFactory(language).Snippets;

    public EditorLanguageProfile GetEditorProfile(string language) =>
        GetFactory(language).EditorProfile;

    public string GetDisplayName(string language) =>
        GetFactory(language).DisplayName;

    // Null for a language that isn't registered, as for one without a template.
    public string? GetCodeForKind(string language, string code, RpadKind kind) =>
        _factories.TryGetValue(language, out var factory) ? factory.CodeForKind(code, kind) : null;

    public ILanguageHost Create(string language, ILanguageHostOptions? options = null) =>
        GetFactory(language).Create(options);

    private ILanguageHostFactory GetFactory(string language)
    {
        if (!_factories.TryGetValue(language, out var factory))
            throw new InvalidOperationException($"No language host registered for '{language}'.");
        return factory;
    }
}
