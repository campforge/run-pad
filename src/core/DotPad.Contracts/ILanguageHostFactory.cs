namespace DotPad.Contracts;

// Marker interface — extend with language-specific settings.
public interface ILanguageHostOptions { }

public interface ILanguageHostFactory
{
    string Language { get; }
    LanguageHostCapabilities Capabilities { get; }
    ILanguageHost Create(ILanguageHostOptions? options = null);
}

public interface ILanguageHostRegistry
{
    IReadOnlyList<string> SupportedLanguages { get; }

    void Register(ILanguageHostFactory factory);
    void Unregister(string language);
    bool IsRegistered(string language);

    LanguageHostCapabilities GetCapabilities(string language);
    ILanguageHost Create(string language, ILanguageHostOptions? options = null);
}
