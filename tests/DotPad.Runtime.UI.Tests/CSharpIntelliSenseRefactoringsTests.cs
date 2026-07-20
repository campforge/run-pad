using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #74 "CodeRefactoringProvider хостинг в лампочке" — GetRefactoringsAsync, the
// position-based counterpart to CodeFixesChanged's diagnostic-driven fixes. Real dotnet-build-free
// integration tests against actual Microsoft.CodeAnalysis.CSharp.Features CodeRefactoringProviders
// (discovered via the same MEF composition RoslynCodeFixEngine already uses for CodeFixProviders),
// not mocked — the thing under test is whether a real provider's CodeAction survives the whole
// pipeline (CodeRefactoringContext -> materialize -> preamble-relocation) intact.
public class CSharpIntelliSenseRefactoringsTests
{
    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    [Fact]
    public async Task GetRefactoringsAsync_Should_OfferUseExpressionBody_ForASimpleReturnOnlyMethod()
    {
        using var service = CreateService();
        const string code = "class C\n{\n    int M()\n    {\n        return 5;\n    }\n}\n";

        // Caret inside the method body, on the `return 5;` line.
        var position = code.IndexOf("return 5;", StringComparison.Ordinal);

        var refactorings = await service.GetRefactoringsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(refactorings);
        var useExpressionBody = Assert.Single(refactorings, r => r.Title.Contains("expression body", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("=> 5", useExpressionBody.NewCode);
        Assert.DoesNotContain("return 5;", useExpressionBody.NewCode);
    }

    [Fact]
    public async Task GetRefactoringsAsync_Should_ReturnEmpty_InsideAComment()
    {
        using var service = CreateService();
        const string code = "// just a comment, nothing refactorable here\n";

        var position = code.IndexOf("comment", StringComparison.Ordinal);

        var refactorings = await service.GetRefactoringsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(refactorings);
        Assert.Empty(refactorings);
    }

    [Fact]
    public async Task GetRefactoringsAsync_Should_ReturnNull_WhenPositionIsOutOfRange()
    {
        using var service = CreateService();
        const string code = "class C { }\n";

        var refactorings = await service.GetRefactoringsAsync(code, code.Length + 100, TestContext.Current.CancellationToken);

        Assert.Null(refactorings);
    }

    // Live report (2026-07-21): CSharpIntroduceVariableService's "Introduce local for all
    // occurrences" throws InvalidCastException (ParenthesizedExpressionSyntax -> SimpleNameSyntax)
    // deep inside Microsoft.CodeAnalysis.CSharp.Features' own generated CSharpSyntaxRewriter when one
    // matched occurrence is syntactically NESTED inside another (here: "p.Name" inside "p.Name.Length"
    // — replacing the inner occurrence briefly produces a MemberAccessExpressionSyntax whose Name slot
    // isn't a SimpleNameSyntax, which the rewriter's own generated cast doesn't tolerate). Reproduced
    // on a bare AdhocWorkspace document with NO DotPad-specific wrapping at all (no preamble, no
    // Script.cs synthetic setup) — confirmed upstream Roslyn 4.13.0 behavior, not something this
    // codebase's document handling triggers or could fix. RoslynCodeFixEngine.TryMaterializeAsync's
    // existing try/catch (the same one CollectFixesAsync's diagnostic-driven fixes already rely on)
    // already covers this — this test locks in that GetRefactoringsAsync itself never surfaces the
    // exception to its caller, it just quietly omits the one broken suggestion.
    [Fact]
    public async Task GetRefactoringsAsync_Should_NotThrow_WhenARefactoringsOwnMaterializationFails()
    {
        using var service = CreateService();
        const string code =
            "class Person { public string Name = \"\"; }\n" +
            "var p = new Person();\n" +
            "System.Console.WriteLine(p.Name.Length);\n" +
            "System.Console.WriteLine(p.Name);\n";

        var position = code.IndexOf(".Name", StringComparison.Ordinal) + 1;

        var refactorings = await service.GetRefactoringsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(refactorings);
        Assert.DoesNotContain(refactorings, r => r.Title.Contains("all occurrences", StringComparison.OrdinalIgnoreCase));
    }
}
