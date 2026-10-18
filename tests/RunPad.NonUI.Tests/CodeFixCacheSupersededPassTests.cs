using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// The lightbulb's entries carry keys into the fix engine's cache of the analysis pass that listed
// them. A pass for older text — superseded, so cancelled, and its results already ignored by the
// editor — used to replace that cache anyway when it had nothing to fix (the only cancellation check
// sat in the per-fix loop), so clicking an entry on screen silently did nothing.
public sealed class CodeFixCacheSupersededPassTests
{
    private const string WithFix = "class C { void M() { var m = Regex.Match(\"a\", \"a\"); } }";
    private const string NothingToFix = "class C { void M() { } }";

    private static Document Document(string code)
    {
        var workspace = new AdhocWorkspace(RunPadRoslynHost.HostServices);
        var referenceService = new CSharpReferenceService();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "P", "P",
            LanguageNames.CSharp, compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: referenceService.GetDefault().Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))));
        return workspace.AddDocument(project.Id, "Script.cs", SourceText.From(code));
    }

    // A superseded pass ends one way or the other; either is fine as long as the fixes on screen stay
    // applicable.
    private static async Task IgnoringCancellation(Task pass)
    {
        try { await pass; }
        catch (OperationCanceledException) { }
    }

    private static async Task<IReadOnlyList<Diagnostic>> DiagnosticsAsync(Document document) =>
        (await document.GetSemanticModelAsync(TestContext.Current.CancellationToken))!.GetDiagnostics();

    [Fact]
    public async Task ACancelledPass_Should_NotReplaceTheFixesOnScreen()
    {
        var engine = new RoslynCodeFixEngine(RunPadRoslynHost.CompositionHost, NullLogger.Instance);
        var listed = Document(WithFix);
        var fixes = await engine.ComputeFixesAsync(listed, await DiagnosticsAsync(listed), WithFix, 0, TestContext.Current.CancellationToken);
        var usingFix = Assert.Single(fixes, f => f.Title == "using System.Text.RegularExpressions;");

        // The next keystroke's pass started: this one is cancelled, but runs to its end.
        using var superseded = new CancellationTokenSource();
        superseded.Cancel();
        var stale = Document(NothingToFix);
        await IgnoringCancellation(engine.ComputeFixesAsync(stale, [], NothingToFix, 0, superseded.Token));

        var applied = await engine.MaterializeAsync(usingFix.Key, WithFix, 0, TestContext.Current.CancellationToken);
        Assert.NotNull(applied);
        // No preamble in this document, so the directive is part of the code itself.
        Assert.StartsWith("using System.Text.RegularExpressions;", applied.Value.NewCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledRefactoringRequest_Should_NotReplaceTheRefactoringsOnScreen()
    {
        var engine = new RoslynCodeFixEngine(RunPadRoslynHost.CompositionHost, NullLogger.Instance);
        const string code = "class C { int M() { return 1 + 2; } }";
        var listed = Document(code);
        var position = code.IndexOf("1 + 2", StringComparison.Ordinal);
        var refactorings = await engine.ComputeRefactoringsAsync(listed, new TextSpan(position, 0), code, 0, 1, TestContext.Current.CancellationToken);
        var first = Assert.IsAssignableFrom<IEnumerable<RunPad.Contracts.IntelliSense.ICodeFixInfo>>(refactorings).First();

        using var superseded = new CancellationTokenSource();
        superseded.Cancel();
        // userLine 0: the request's caret is before the user's code — the early path.
        await IgnoringCancellation(engine.ComputeRefactoringsAsync(Document(code), new TextSpan(0, 0), code, 0, 0, superseded.Token));

        Assert.NotNull(await engine.MaterializeAsync(first.Key, code, 0, TestContext.Current.CancellationToken));
    }
}
