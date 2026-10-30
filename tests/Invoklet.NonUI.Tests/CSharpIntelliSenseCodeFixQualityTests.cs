using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Live user report (2026-07-27) — three quick fixes to the lightbulb/code-fix pipeline (real Roslyn
// CodeFixProviders/CodeRefactoringProviders, not mocked — same rationale as every other
// CSharpIntelliSense*Tests file: the thing under test is whether Invoklet's synthetic preamble/kind-
// wrapped document survives real provider execution intact).
public class CSharpIntelliSenseCodeFixQualityTests
{
    private static readonly TimeSpan BackgroundAnalysisTimeout = TimeSpan.FromSeconds(30);

    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    // "offers to generate a constructor for '__InvokletProgram'" — MdRunKind.Program wraps the user's code in
    // a synthetic class (MdRunKindTransform.ProgramWrapperClassName) that's a REAL class in the live
    // analysis document, never shown to the user. Confirmed via a live probe (before the fix, with
    // RoslynCodeFixEngine's filter temporarily disabled) that caret position 0 — on the "int x;" field
    // declaration below — makes Roslyn's own generate-constructor refactoring fire against it,
    // producing the exact literal titles "Generate constructor '__InvokletProgram(int x)'" and
    // "Generate constructor '__InvokletProgram()'".
    [Fact]
    public async Task GetRefactoringsAsync_Should_NotOfferToGenerateAConstructor_OnTheSyntheticProgramWrapperClass()
    {
        using var service = CreateService();
        service.UpdateKind(MdRunKind.Program);
        const string code = "int x;\n\nvoid Main()\n{\n    Console.WriteLine(x);\n}\n";

        var refactorings = await service.GetRefactoringsAsync(code, 0, TestContext.Current.CancellationToken);

        Assert.NotNull(refactorings);
        Assert.DoesNotContain(refactorings, r => r.Title.Contains("__InvokletProgram", StringComparison.Ordinal));
    }

    // "If on one line ... JsonSerializer.Serialize(result, new JsonSerializerOptions {}...) then the
    // import gets suggested twice" — JsonSerializer and JsonSerializerOptions are two SEPARATE
    // undefined-type diagnostics on the same line, both fixed by the same `using System.Text.Json;` —
    // each independently discovered and offered that fix, so the lightbulb showed it twice.
    [Fact]
    public async Task NotifyTextChanged_Should_OfferTheSameUsingFix_OnlyOnce_WhenTwoUndefinedTypesOnOneLineNeedTheSameNamespace()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<ICodeFixInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CodeFixesChanged += (_, f) =>
        {
            if (f.Any(x => x.Title.Contains("System.Text.Json", StringComparison.Ordinal))) tcs.TrySetResult(f);
        };

        service.NotifyTextChanged(
            "var output = JsonSerializer.Serialize(new { a = 1 }, new JsonSerializerOptions());\n");

        var fixes = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        // Exact title match, not a substring — Roslyn also independently offers "Fully Qualify" fixes
        // for each occurrence ("System.Text.Json.JsonSerializer", "System.Text.Json.JsonSerializerOptions")
        // that legitimately mention the namespace too but are NOT duplicates of each other or of the
        // "using" fix — only the true duplicate ("using System.Text.Json;", offered independently by
        // both undefined-type diagnostics) is what this test is about.
        var usingFixes = fixes.Where(f => f.Title == "using System.Text.Json;").ToList();

        var onlyLine = Assert.Single(usingFixes);
        Assert.Equal(1, onlyLine.Line);
    }

    // "like in ReSharper ... a 'using all' that automatically resolves all imports" — ResolveAllMissingUsingsAsync
    // resolves every unambiguous unresolved-type diagnostic in one pass instead of the lightbulb's fix
    // one at a time. Three undefined types, two different (but each individually unambiguous)
    // namespaces, on three different lines.
    [Fact]
    public async Task ResolveAllMissingUsingsAsync_Should_ResolveEveryUnambiguousMissingType_InOnePass()
    {
        using var service = CreateService();
        const string code =
            "var list = new List<int>();\n" +
            "var output = JsonSerializer.Serialize(list, new JsonSerializerOptions());\n" +
            "var sb = new StringBuilder();\n";

        var resolved = await service.ResolveAllMissingUsingsAsync(code, TestContext.Current.CancellationToken);

        Assert.Equal(code, resolved.NewCode); // never touches the visible text, only usings
        Assert.Contains("System.Text.Json", resolved.UsingsToAdd);
        Assert.Contains("System.Text", resolved.UsingsToAdd);
        // List<int> is covered by ImplicitUsings.Default (System.Collections.Generic) already —
        // nothing to resolve for that line, and nothing should be added redundantly for it.
        Assert.Equal(2, resolved.UsingsToAdd.Count);
    }

    [Fact]
    public async Task ResolveAllMissingUsingsAsync_Should_ReturnNoUsings_WhenNothingIsUnresolved()
    {
        using var service = CreateService();
        const string code = "Console.WriteLine(\"hi\");\n";

        var resolved = await service.ResolveAllMissingUsingsAsync(code, TestContext.Current.CancellationToken);

        Assert.Empty(resolved.UsingsToAdd);
    }

    // A genuinely ambiguous unresolved type (more than one equally-valid candidate namespace) must be
    // left alone rather than guessing — silently picking the wrong one would be worse than not fixing
    // it at all. "Timer" resolves to both System.Threading.Timer and System.Timers.Timer with no other
    // hint to disambiguate by.
    [Fact]
    public async Task ResolveAllMissingUsingsAsync_Should_SkipAnAmbiguousType_RatherThanGuessing()
    {
        using var service = CreateService();
        const string code = "Timer t = null;\n";

        var resolved = await service.ResolveAllMissingUsingsAsync(code, TestContext.Current.CancellationToken);

        Assert.Empty(resolved.UsingsToAdd);
    }
}
