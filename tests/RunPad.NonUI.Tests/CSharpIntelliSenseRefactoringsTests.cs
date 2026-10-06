using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #74 "CodeRefactoringProvider hosting in the lightbulb" — GetRefactoringsAsync, the
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

        // TODO.md #74 "IntelliSenseService optimization" (pass 104): NewCode is no longer
        // on the listed candidate itself — materializing it (running the refactoring provider's own
        // document transform + diffing) is deferred to MaterializeFixAsync, called only for the one
        // candidate actually clicked.
        var materialized = await service.MaterializeFixAsync(code, useExpressionBody.Key, TestContext.Current.CancellationToken);
        Assert.NotNull(materialized);
        Assert.Contains("=> 5", materialized.NewCode);
        Assert.DoesNotContain("return 5;", materialized.NewCode);
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
    // on a bare AdhocWorkspace document with NO RunPad-specific wrapping at all (no preamble, no
    // Script.cs synthetic setup) — confirmed upstream Roslyn 4.13.0 behavior, not something this
    // codebase's document handling triggers or could fix.
    //
    // TODO.md #74 "IntelliSenseService optimization" (pass 104) changed WHERE this gets
    // caught: listing candidates (GetRefactoringsAsync) no longer materializes anything — Register/
    // ComputeRefactoringsAsync alone never trips this bug (it only lives inside GetOperationsAsync,
    // now deferred) — so the broken suggestion DOES appear in the list now, same as any other
    // candidate. MaterializeFixAsync (the click-time half) is what actually runs GetOperationsAsync,
    // and RoslynCodeFixEngine.TryMaterializeAsync's existing try/catch still covers it there —
    // clicking this specific broken suggestion safely does nothing (materializes to null) rather than
    // never having been offered in the first place. A user seeing a menu item that turns out to do
    // nothing on click is a real, minor regression from the old eager-materialization behavior — an
    // accepted tradeoff for not paying GetOperationsAsync's cost for all ~63 providers' candidates on
    // every caret move (see RoslynCodeFixEngine's own class doc comment for the measured cost this
    // avoids) — worth exactly one broken upstream refactoring provider silently doing nothing when
    // clicked, not worth eagerly materializing every candidate just to filter out the rare no-op ones
    // in advance.
    [Fact]
    public async Task GetRefactoringsAsync_Should_ListEvenAOwnMaterializationFailingRefactoring_ButMaterializeSafelyToNull()
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

        var broken = Assert.Single(refactorings, r => r.Title.Contains("all occurrences", StringComparison.OrdinalIgnoreCase));

        var materialized = await service.MaterializeFixAsync(code, broken.Key, TestContext.Current.CancellationToken);
        Assert.Null(materialized);
    }

    // TODO.md #74 "IntelliSenseService optimization" (pass 104) — RoslynCodeFixEngine's
    // candidate cache is wholesale-replaced on every new ComputeFixesAsync/ComputeRefactoringsAsync
    // call (its own class doc comment), not merged/appended — a key from an OLDER listing pass simply
    // isn't in the CURRENT cache once a newer one has run. Same "gracefully do nothing" contract as a
    // key that never existed at all — there's no way for the caller to tell the two apart, and no
    // reason to: either way, re-materializing against outdated state wouldn't be meaningful.
    [Fact]
    public async Task MaterializeFixAsync_Should_ReturnNull_ForAKeyFromAnOlderListingPass()
    {
        using var service = CreateService();
        const string code = "class C\n{\n    int M()\n    {\n        return 5;\n    }\n}\n";
        var position = code.IndexOf("return 5;", StringComparison.Ordinal);

        var first = await service.GetRefactoringsAsync(code, position, TestContext.Current.CancellationToken);
        var staleKey = Assert.Single(first!, r => r.Title.Contains("expression body", StringComparison.OrdinalIgnoreCase)).Key;

        // A second listing pass at a different position replaces the cache the first pass populated.
        await service.GetRefactoringsAsync(code, 0, TestContext.Current.CancellationToken);

        var materialized = await service.MaterializeFixAsync(code, staleKey, TestContext.Current.CancellationToken);
        Assert.Null(materialized);
    }

    // TODO.md pass 102 note: Roslyn's MoveTypeCodeRefactoringProvider offered "Move type to C.cs",
    // "Rename file to C.cs" and — keyed to the synthetic document's name — "Rename type to Script" on
    // every type declaration. A .rpad script is one file the user never sees as Script.cs.
    [Theory]
    [InlineData("class C { }\n")]
    [InlineData("Console.WriteLine(1);\nclass C { }\n")]
    public async Task GetRefactoringsAsync_Should_NotOfferFileBasedMoveTypeRefactorings(string code)
    {
        using var service = CreateService();
        var position = code.IndexOf("class C", StringComparison.Ordinal) + "class ".Length;

        var refactorings = await service.GetRefactoringsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(refactorings);
        Assert.NotEmpty(refactorings); // the caret really is somewhere refactorable ("Generate constructor", ...)
        Assert.DoesNotContain(refactorings, r => r.Title.Contains("Script", StringComparison.Ordinal));
        Assert.DoesNotContain(refactorings, r => r.Title.StartsWith("Move type to", StringComparison.Ordinal));
        Assert.DoesNotContain(refactorings, r => r.Title.StartsWith("Rename file to", StringComparison.Ordinal));
    }

    // Every listed refactoring must actually do something when clicked. CodeActionWithOptions
    // ("Generate overrides...") needs a host options dialog RunPad doesn't have, so it used to be
    // listed and then materialize to null — a lightbulb item that silently did nothing.
    [Fact]
    public async Task GetRefactoringsAsync_Should_OnlyListRefactoringsThatMaterialize()
    {
        using var service = CreateService();
        const string code = "class Base { public virtual void M() { } }\nclass C : Base { }\n";
        var position = code.IndexOf("class C", StringComparison.Ordinal) + "class ".Length;

        var refactorings = await service.GetRefactoringsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(refactorings);
        Assert.NotEmpty(refactorings);
        foreach (var refactoring in refactorings)
        {
            var materialized = await service.MaterializeFixAsync(code, refactoring.Key, TestContext.Current.CancellationToken);
            Assert.True(materialized is not null, $"'{refactoring.Title}' is listed but materializes to nothing");
        }
    }
}
