namespace DotPad.Contracts;

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

    public ILanguageHost Create(string language, ILanguageHostOptions? options = null) =>
        GetFactory(language).Create(options);

    private ILanguageHostFactory GetFactory(string language)
    {
        if (!_factories.TryGetValue(language, out var factory))
            throw new InvalidOperationException($"No language host registered for '{language}'.");
        return factory;
    }
}
