using Moq;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Format.RPad;

namespace RunPad.Runtime.UI.Tests;

// Editor services for the tabs a test opens, supplied the way the app supplies them: by the
// registered language, per tab.
internal static class TestLanguageRegistry
{
    // C# tabs get what create returns; tabs in any other language get none, as with the real modules.
    public static ILanguageHostRegistry WithCSharpIntelliSense(this Mock<ILanguageHostRegistry> registry, Func<IIntelliSenseService> create)
    {
        registry.Setup(r => r.CreateIntelliSense("csharp", It.IsAny<CancellationToken>())).Returns(create);
        return registry.Object;
    }

    // A real language whose editor services are replaced, everything else unchanged.
    public static ILanguageHostFactory WithIntelliSense(this ILanguageHostFactory factory, Func<IIntelliSenseService> create)
        => new IntelliSenseOverride(factory, create);

    private sealed class IntelliSenseOverride(ILanguageHostFactory inner, Func<IIntelliSenseService> create) : ILanguageHostFactory
    {
        public string Language => inner.Language;
        public string DisplayName => inner.DisplayName;
        public LanguageHostCapabilities Capabilities => inner.Capabilities;
        public IReadOnlyDictionary<string, string> Snippets => inner.Snippets;
        public EditorLanguageProfile EditorProfile => inner.EditorProfile;
        public string? CodeForKind(string code, RpadKind kind) => inner.CodeForKind(code, kind);
        public IIntelliSenseService? CreateIntelliSense(CancellationToken cancellationToken) => create();
        public ILanguageHost Create(ILanguageHostOptions? options = null) => inner.Create(options);
    }
}
