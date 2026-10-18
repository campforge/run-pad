using RunPad.Contracts.IntelliSense;
using RunPad.Format.RPad;

namespace RunPad.Contracts;

public sealed class LanguageHostRegistry : ILanguageHostRegistry
{
    private readonly Dictionary<string, ILanguageHostFactory> _factories =
        new(StringComparer.OrdinalIgnoreCase);
    // Registration order, which Dictionary doesn't promise once a language is unregistered.
    private readonly List<string> _order = [];

    public IReadOnlyList<string> SupportedLanguages => [.. _order];

    public IReadOnlyList<LanguageDescriptor> Languages =>
    [
        .. _order.Select(id => _factories[id]).Select(f =>
            new LanguageDescriptor(f.Language, f.DisplayName, f.Capabilities, f.EditorProfile.IconResourceKey)),
    ];

    public void Register(ILanguageHostFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        // Registering a language again replaces its factory in place.
        if (!_factories.ContainsKey(factory.Language)) _order.Add(factory.Language);
        _factories[factory.Language] = factory;
    }

    public void Unregister(string language)
    {
        if (_factories.Remove(language))
            _order.RemoveAll(id => string.Equals(id, language, StringComparison.OrdinalIgnoreCase));
    }

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

    public IIntelliSenseService? CreateIntelliSense(string language, CancellationToken cancellationToken) =>
        _factories.TryGetValue(language, out var factory) ? factory.CreateIntelliSense(cancellationToken) : null;

    public void WarmUpIntelliSense()
    {
        foreach (var id in _order.ToList()) _factories[id].WarmUpIntelliSense();
    }

    public ILanguageHost Create(string language, ILanguageHostOptions? options = null) =>
        GetFactory(language).Create(options);

    private ILanguageHostFactory GetFactory(string language)
    {
        if (!_factories.TryGetValue(language, out var factory))
            throw new InvalidOperationException($"No language host registered for '{language}'.");
        return factory;
    }
}
