using DotPad.Contracts;

namespace DotPad.Languages.CSharp;

public sealed class CSharpLanguageHostFactory : ILanguageHostFactory
{
    public string Language => "csharp";

    public LanguageHostCapabilities Capabilities =>
        LanguageHostCapabilities.Run               |
        LanguageHostCapabilities.Debug             |
        LanguageHostCapabilities.Breakpoints       |
        LanguageHostCapabilities.VariableInspection|
        LanguageHostCapabilities.MemoryDump;

    public ILanguageHost Create(ILanguageHostOptions? options = null)
    {
        var opts = options as CSharpLanguageHostOptions
            ?? throw new ArgumentException(
                $"Expected {nameof(CSharpLanguageHostOptions)}.", nameof(options));
        return new CSharpLanguageHost(opts);
    }
}
