using Moq;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Contracts.Models;
using Invoklet.Format.RPad;
using Invoklet.Languages.DotnetCommon;

namespace Invoklet.Runtime.UI.Tests;

// Editor services for the tabs a test opens, supplied the way the app supplies them: by the
// registered language, per tab.
internal static class TestLanguageRegistry
{
    // C# tabs get what create returns; tabs in any other language get none, as with the real modules.
    // C# tabs are highlighted as C#, with an otherwise empty editor profile.
    public static ILanguageHostRegistry WithCSharpIntelliSense(this Mock<ILanguageHostRegistry> registry, Func<IIntelliSenseService> create)
    {
        registry.Setup(r => r.CreateIntelliSense("csharp", It.IsAny<CancellationToken>())).Returns(create);
        registry.Setup(r => r.GetEditorProfile("csharp")).Returns(new EditorLanguageProfile { SourceFileExtension = ".cs" });
        return registry.Object;
    }

    // A fake editor service that reads the tab's execution request the way the C# service does
    // (DotnetPackageReferenceContext), so a test can check the references and target it ends up with.
    public static Mock<IIntelliSenseService> ForwardingExecutionContext(this Mock<IIntelliSenseService> service)
    {
        service.Setup(s => s.ApplyExecutionContext(It.IsAny<ExecutionRequest>(), It.IsAny<CancellationToken>())).Callback<ExecutionRequest, CancellationToken>((request, _) =>
        {
            var packages = request.Packages.ToDictionary(p => p.Key, p => new Package(p.Value));
            if (DotnetPackageReferenceContext.For(request) is { } context)
                service.Object.UpdatePackageReferences(packages, context);
            else
                service.Object.UpdatePackageReferences(packages);
        });
        return service;
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
        public string? CodeForKind(string code, RpadKind kind, string indent = "    ") => inner.CodeForKind(code, kind, indent);
        public IIntelliSenseService? CreateIntelliSense(CancellationToken cancellationToken) => create();
        public ILanguageHost Create(ILanguageHostOptions? options = null) => inner.Create(options);
        public ILanguageHost Create(ExecutionRequest request) => inner.Create(request);
    }
}
