using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// GetRefactoringsAsync, the
// position-based counterpart to CodeFixesChanged's diagnostic-driven fixes. Real dotnet-build-free
// integration tests against actual Microsoft.CodeAnalysis.CSharp.Features CodeRefactoringProviders
// (discovered via the same MEF composition RoslynCodeFixEngine already uses for CodeFixProviders),
// not mocked — the thing under test is whether a real provider's CodeAction survives the whole
// pipeline (CodeRefactoringContext -> materialize -> preamble-relocation) intact.
public class CSharpIntelliSenseRefactoringsTests
{
    private static CSharpIntelliSenseService CreateService(ILogger<CSharpIntelliSenseService>? logger = null)
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, logger ?? NullLogger<CSharpIntelliSenseService>.Instance);
    }

    // Keeps RoslynCodeFixEngine's debug log (raw TextChanges, which discard branch fired, swallowed
    // exceptions) so a failure that only reproduces on another OS reports why, not just that it failed.
    private sealed class RecordingLogger : ILogger<CSharpIntelliSenseService>
    {
        private readonly List<string> _lines = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
        }

        public string Drain()
        {
            lock (_lines)
            {
                var text = string.Join("\n", _lines);
                _lines.Clear();
                return text;
            }
        }
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

        // NewCode is no longer
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

    // Live report (2026-07-21): under Roslyn 4.13, CSharpIntroduceVariableService's "Introduce local
    // for all occurrences" threw InvalidCastException deep inside Microsoft.CodeAnalysis.CSharp.Features
    // when one matched occurrence was NESTED inside another ("p.Name" inside "p.Name.Length"). Listing
    // candidates never materializes anything, so the broken item was listed and
    // RoslynCodeFixEngine.TryMaterializeAsync's try/catch made clicking it a safe no-op.
    //
    // Roslyn 5.9 fixed the upstream bug: the same refactoring (offered on the "." of "p.Name") now
    // materializes. This pins that the nested-occurrence case works end to end; the try/catch stays
    // as the guard for any other provider that throws while computing its operations.
    [Fact]
    public async Task GetRefactoringsAsync_Should_ListAndMaterialize_IntroduceLocalForAllNestedOccurrences()
    {
        using var service = CreateService();
        const string code =
            "class Person { public string Name = \"\"; }\n" +
            "var p = new Person();\n" +
            "System.Console.WriteLine(p.Name.Length);\n" +
            "System.Console.WriteLine(p.Name);\n";

        var position = code.IndexOf(".Name", StringComparison.Ordinal);

        var refactorings = await service.GetRefactoringsAsync(code, position, TestContext.Current.CancellationToken);
        Assert.NotNull(refactorings);

        var allOccurrences = Assert.Single(refactorings,
            r => r.Title.Contains("all occurrences of 'p.Name'", StringComparison.OrdinalIgnoreCase));

        var materialized = await service.MaterializeFixAsync(code, allOccurrences.Key, TestContext.Current.CancellationToken);
        Assert.NotNull(materialized);
    }

    // RoslynCodeFixEngine's
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

    // Roslyn's MoveTypeCodeRefactoringProvider offered "Move type to C.cs",
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
    // ("Generate overrides...") needs a host options dialog Invoklet doesn't have, so it used to be
    // listed and then materialize to null — a lightbulb item that silently did nothing.
    [Fact]
    public async Task GetRefactoringsAsync_Should_OnlyListRefactoringsThatMaterialize()
    {
        var log = new RecordingLogger();
        using var service = CreateService(log);
        const string code = "class Base { public virtual void M() { } }\nclass C : Base { }\n";
        var position = code.IndexOf("class C", StringComparison.Ordinal) + "class ".Length;

        var refactorings = await service.GetRefactoringsAsync(code, position, TestContext.Current.CancellationToken);

        Assert.NotNull(refactorings);
        Assert.NotEmpty(refactorings);
        foreach (var refactoring in refactorings)
        {
            log.Drain();
            var materialized = await service.MaterializeFixAsync(code, refactoring.Key, TestContext.Current.CancellationToken);
            Assert.True(materialized is not null,
                $"'{refactoring.Title}' is listed but materializes to nothing. Engine log:\n{log.Drain()}");
        }
    }

    // A fix that adds a using to the hidden preamble can arrive, depending on the Roslyn version the
    // installed SDK brings, as a minimal character diff over the following lines instead of one
    // inserted line — this is the shape newer SDKs produced for "Add 'DebuggerDisplay' attribute".
    private const string PreambleSample =
        "using System;\nusing System.Collections.Generic;\nusing System.IO;\nusing System.Linq;\nusing System.Net.Http;\n";

    private static TextChange Replace(string text, string find, string replacement)
    {
        var start = text.IndexOf(find, StringComparison.Ordinal);
        return new TextChange(new TextSpan(start, find.Length), replacement);
    }

    [Fact]
    public void UsingsInsertedIntoPreamble_Should_RecognizeAUsingSortedInAsACharacterDiff()
    {
        var changes = new[]
        {
            Replace(PreambleSample, "IO;", "Diagnostics;"),
            Replace(PreambleSample, "Linq;", "IO;"),
            Replace(PreambleSample, "Net.Http;", "Linq;"),
            new TextChange(new TextSpan(PreambleSample.Length, 0), "using System.Net.Http;\n"),
        };

        var usings = RoslynCodeFixEngine.UsingsInsertedIntoPreamble(PreambleSample, changes);

        Assert.Equal(["System.Diagnostics"], usings);
    }

    [Fact]
    public void UsingsInsertedIntoPreamble_Should_AcceptAPlainLineInsertion()
    {
        var at = PreambleSample.IndexOf("using System.IO;", StringComparison.Ordinal);

        var usings = RoslynCodeFixEngine.UsingsInsertedIntoPreamble(
            PreambleSample, [new TextChange(new TextSpan(at, 0), "using System.Diagnostics;\n")]);

        Assert.Equal(["System.Diagnostics"], usings);
    }

    [Fact]
    public void UsingsInsertedIntoPreamble_Should_RejectChangingAnExistingUsing()
    {
        var usings = RoslynCodeFixEngine.UsingsInsertedIntoPreamble(
            PreambleSample, [Replace(PreambleSample, "IO;", "Diagnostics;")]);

        Assert.Null(usings);
    }

    [Fact]
    public void UsingsInsertedIntoPreamble_Should_RejectInsertingSomethingOtherThanAUsing()
    {
        var usings = RoslynCodeFixEngine.UsingsInsertedIntoPreamble(
            PreambleSample, [new TextChange(new TextSpan(0, 0), "[assembly: System.CLSCompliant(true)]\n")]);

        Assert.Null(usings);
    }
}
