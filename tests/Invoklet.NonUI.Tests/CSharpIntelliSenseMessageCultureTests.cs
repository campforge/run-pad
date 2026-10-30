using System.Globalization;
using System.Text.RegularExpressions;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Invoklet.NonUI.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Invoklet's interface is English, but Roslyn localizes its diagnostics and code-action titles by
// the UI culture of the thread that produces them — on a Russian macOS the lightbulb and the error
// squiggles came out in Russian. These run the real service with ru-RU as the UI culture (and as the
// culture, which must keep formatting numbers its own way) and restore both afterwards.
[Collection(UiCultureCollection.Name)]
public sealed class CSharpIntelliSenseMessageCultureTests : IDisposable
{
    private static readonly TimeSpan BackgroundAnalysisTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex Cyrillic = new(@"\p{IsCyrillic}");

    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
    private readonly CultureInfo? _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public CSharpIntelliSenseMessageCultureTests()
    {
        var russian = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = russian;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = russian;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
        CultureInfo.DefaultThreadCurrentCulture = _defaultCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
    }

    [Fact]
    public void TheTestRuns_UnderARussianUiCulture()
    {
        Assert.Equal("ru-RU", CultureInfo.CurrentUICulture.Name);
    }

    // Squiggle messages, raised from the background analysis (another thread).
    [Fact]
    public async Task Diagnostics_Should_BeInEnglish()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) => { if (d.Count > 0) tcs.TrySetResult(d); };

        service.NotifyTextChanged("int x = \"text\";\nundefinedName++;\n");
        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);

        Assert.All(diagnostics, d => Assert.DoesNotMatch(Cyrillic, d.Message));
        Assert.Contains(diagnostics, d => d.Message.Contains("Cannot implicitly convert type 'string' to 'int'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("does not exist in the current context", StringComparison.Ordinal));
    }

    // Lightbulb titles for errors (code fixes), from the background analysis too.
    [Fact]
    public async Task CodeFixTitles_Should_BeInEnglish()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        var tcs = new TaskCompletionSource<IReadOnlyList<ICodeFixInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CodeFixesChanged += (_, f) => { if (f.Count > 1) tcs.TrySetResult(f); };

        service.NotifyTextChanged("var output = JsonSerializer.Serialize(1);\nint total = Compute(2);\n");
        var fixes = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);

        Assert.All(fixes, f => Assert.DoesNotMatch(Cyrillic, f.Title));
        Assert.Contains(fixes, f => f.Title.StartsWith("Generate method", StringComparison.Ordinal));
    }

    // Refactoring titles, asked for directly (await).
    [Fact]
    public async Task RefactoringTitles_Should_BeInEnglish()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();

        var refactorings = await service.GetRefactoringsAsync("var count = 42;\nConsole.WriteLine(count);\n", 1, TestContext.Current.CancellationToken);

        Assert.NotNull(refactorings);
        Assert.NotEmpty(refactorings);
        Assert.All(refactorings, r => Assert.DoesNotMatch(Cyrillic, r.Title));
        Assert.Contains(refactorings, r => r.Title == "Inline temporary variable");
    }

    // The scripts' and the app's own formatting culture is left as it is.
    [Fact]
    public async Task TheCurrentCulture_Should_StayRussian_AroundTheService()
    {
        using var service = new CSharpIntelliSenseFixture().CreateSut();
        await service.GetRefactoringsAsync("var count = 42;\n", 1, TestContext.Current.CancellationToken);

        Assert.Equal("ru-RU", CultureInfo.CurrentCulture.Name);
        Assert.Equal("ru-RU", CultureInfo.CurrentUICulture.Name);
        Assert.Equal("1,5", 1.5.ToString(CultureInfo.CurrentCulture));
    }
}

// Tests that change the process-wide default cultures run one at a time.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiCultureCollection
{
    public const string Name = "UI culture";
}
