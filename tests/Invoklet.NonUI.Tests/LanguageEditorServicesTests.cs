using Microsoft.Extensions.Logging.Abstractions;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.RPad;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.VB;
using Invoklet.Services;
using Xunit;

namespace Invoklet.NonUI.Tests;

// Languages describe themselves through the registry, and each creates its own editor services:
// the app neither lists languages nor builds a language's services itself.
public sealed class LanguageEditorServicesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("Invoklet-language-services").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FakeLanguage(string id, string name = "Fake") : ILanguageHostFactory
    {
        public int WarmUps;
        public string Language => id;
        public string DisplayName => name;
        public LanguageHostCapabilities Capabilities => LanguageHostCapabilities.Run;
        public IReadOnlyDictionary<string, string> Snippets { get; } = new Dictionary<string, string>();
        public EditorLanguageProfile EditorProfile { get; } = new() { IconResourceKey = "Icon." + id };
        public IIntelliSenseService? Service { get; init; }
        public IIntelliSenseService? CreateIntelliSense(CancellationToken cancellationToken) => Service;
        public void WarmUpIntelliSense() => WarmUps++;
        public ILanguageHost Create(ILanguageHostOptions? options = null) => throw new NotSupportedException();
    }

    private static LanguageHostRegistry Registry(params ILanguageHostFactory[] factories)
    {
        var registry = new LanguageHostRegistry();
        foreach (var factory in factories) registry.Register(factory);
        return registry;
    }

    private static LanguageHostRegistry RealRegistry() => Registry(
        new CSharpLanguageHostFactory(NullLoggerFactory.Instance), new VbLanguageHostFactory(NullLoggerFactory.Instance));

    [Fact]
    public void TheRegistry_Should_DescribeItsLanguages_InRegistrationOrder()
    {
        var registry = Registry(new FakeLanguage("vb", "Visual Basic"), new FakeLanguage("csharp", "C#"), new FakeLanguage("fs", "F#"));

        Assert.Equal(
            [
                new LanguageDescriptor("vb", "Visual Basic", LanguageHostCapabilities.Run, "Icon.vb"),
                new LanguageDescriptor("csharp", "C#", LanguageHostCapabilities.Run, "Icon.csharp"),
                new LanguageDescriptor("fs", "F#", LanguageHostCapabilities.Run, "Icon.fs"),
            ],
            registry.Languages);

        // Registering a language again keeps its place; unregistering removes it.
        registry.Register(new FakeLanguage("VB", "Basic"));
        registry.Unregister("fs");
        Assert.Equal(["Basic", "C#"], registry.Languages.Select(l => l.DisplayName));
        Assert.Equal(["vb", "csharp"], registry.SupportedLanguages);
    }

    [Fact]
    public void TheRealLanguages_Should_BeDescribed_AsTheMenusListedThem()
    {
        var languages = RealRegistry().Languages;

        Assert.Equal(["csharp", "vb"], languages.Select(l => l.Id));
        Assert.Equal(["C#", "Visual Basic"], languages.Select(l => l.DisplayName));
        Assert.Equal(["Icon.LanguageCSharp", "Icon.LanguageVb"], languages.Select(l => l.IconResourceKey));
        Assert.True(languages[0].Capabilities.HasFlag(LanguageHostCapabilities.IntelliSense));
        Assert.False(languages[1].Capabilities.HasFlag(LanguageHostCapabilities.IntelliSense));
    }

    [Fact]
    public void TheRegistry_Should_CreateEditorServices_FromTheLanguage_AndNoneForAnUnknownOne()
    {
        using var service = new CSharpIntelliSenseService([], NullLogger<CSharpIntelliSenseService>.Instance);
        var fake = new FakeLanguage("fake") { Service = service };
        var registry = Registry(fake, new FakeLanguage("plain"));

        Assert.Same(service, registry.CreateIntelliSense("fake", CancellationToken.None));
        Assert.Null(registry.CreateIntelliSense("plain", CancellationToken.None));
        Assert.Null(registry.CreateIntelliSense("unknown", CancellationToken.None));
    }

    [Fact]
    public void WarmingUp_Should_WarmEveryLanguageOnce()
    {
        var (a, b) = (new FakeLanguage("a"), new FakeLanguage("b"));

        Registry(a, b).WarmUpIntelliSense();

        Assert.Equal((1, 1), (a.WarmUps, b.WarmUps));
    }

    // C# builds a new, independent service on every call — one per tab — with Invoklet.Runtime given
    // apart from the framework (so a target's reference assemblies never replace it). VB has none.
    [Fact]
    public void TheCSharpLanguage_Should_CreateANewServicePerCall_WithTheLocatedRuntimeLibrary()
    {
        var runtime = Path.Combine(_root, "Invoklet.Runtime.dll");
        File.Copy(DotnetRuntimeReferenceLocator.RuntimeAssemblyPath, runtime);
        var csharp = new CSharpLanguageHostFactory(NullLoggerFactory.Instance, new FixedLocator(runtime));

        using var first = Assert.IsType<CSharpIntelliSenseService>(csharp.CreateIntelliSense(CancellationToken.None));
        using var second = Assert.IsType<CSharpIntelliSenseService>(csharp.CreateIntelliSense(CancellationToken.None));

        Assert.NotSame(first, second);
        Assert.Contains(runtime, first.ReferencePathsForTests);
        Assert.Contains(runtime, second.ReferencePathsForTests);
        Assert.Contains(first.ReferencePathsForTests, p => Path.GetFileName(p) == "System.Runtime.dll");
        ILanguageHostFactory vb = new VbLanguageHostFactory(NullLoggerFactory.Instance);
        Assert.Null(vb.CreateIntelliSense(CancellationToken.None));
    }

    [Fact]
    public void ACancelledCreation_Should_StopBeforeBuildingAService()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new CSharpLanguageHostFactory(NullLoggerFactory.Instance).CreateIntelliSense(cancelled.Token));
    }

    private sealed class FixedLocator(string path) : IRuntimeReferenceLocator
    {
        public string GetRuntimeAssemblyPath() => path;
    }

    // The menus' New File choices: the registered languages the .rpad format can store, as listed before.
    [Fact]
    public void TheNewFileChoices_Should_BeTheRegisteredLanguagesAFileCanHold()
    {
        Assert.Equal(
            [
                new NewFileLanguageChoice(RpadLanguage.CSharp, "C#", "Icon.LanguageCSharp"),
                new NewFileLanguageChoice(RpadLanguage.Vb, "Visual Basic", "Icon.LanguageVb"),
            ],
            NewFileLanguageChoice.From(RealRegistry()));

        var withUnknown = Registry(new FakeLanguage("fs", "F#"), new FakeLanguage("vb", "Visual Basic"));
        Assert.Equal([RpadLanguage.Vb], NewFileLanguageChoice.From(withUnknown).Select(c => c.Language));
    }

    [Theory]
    [InlineData("csharp", RpadLanguage.CSharp)]
    [InlineData("VB", RpadLanguage.Vb)]
    public void ALanguageId_Should_NameItsFormatValue(string id, RpadLanguage expected)
    {
        Assert.True(RpadLanguageExtensions.TryFromLanguageId(id, out var language));
        Assert.Equal(expected, language);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("fs")]
    [InlineData("")]
    public void AnIdWithoutAFormatValue_Should_NameNone(string id)
    {
        Assert.False(RpadLanguageExtensions.TryFromLanguageId(id, out _));
    }
}
