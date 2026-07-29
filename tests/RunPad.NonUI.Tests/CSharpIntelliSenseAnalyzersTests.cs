using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Contracts.IntelliSense;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #74 "Пайплайн анализаторов" (сто третий проход) — RoslynAnalyzerEngine, wired into
// NotifyTextChanged's existing background pass alongside the compiler's own diagnostics (see
// CSharpIntelliSenseBackgroundAnalysisTests.cs for that shared pass's other coverage). Real
// Microsoft.CodeAnalysis.CSharp.Features IDE0xxx analyzers, not mocked — same rationale as every
// other "real Roslyn, not a fake" test this session: the thing under test is whether RunPad's
// synthetic preamble-wrapped document survives analyzer execution intact, not something a mock could
// stand in for.
public class CSharpIntelliSenseAnalyzersTests
{
    private static readonly TimeSpan BackgroundAnalysisTimeout = TimeSpan.FromSeconds(30);

    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    // IDE0044 ("Make field readonly") fires at Info severity, not Hidden — a real, visible-squiggle
    // analyzer diagnostic the compiler itself would never produce on its own (no compiler warning for
    // "this field could be readonly").
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForAnAnalyzerFinding()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { private int count = 0; void M() { System.Console.WriteLine(count); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("readonly", StringComparison.OrdinalIgnoreCase));
    }

    // IDE0040 ("Accessibility modifiers required") fires at Hidden severity — invisible as a
    // squiggle, but its existence is what makes CSharpAddAccessibilityModifiersCodeFixProvider (a
    // real CodeFixProvider, already discovered by RoslynCodeFixEngine since the проход before this
    // one) able to fire at all. Before this проход, ComputeDiagnosticsAndCodeFixesAsync only ever
    // computed COMPILER diagnostics — the C# compiler doesn't warn about missing accessibility
    // modifiers (implicit `private` is perfectly legal C#), so this fix could never have appeared,
    // full stop, regardless of anything RoslynCodeFixEngine itself does right.
    //
    // (Deliberately NOT "remove unnecessary using" — investigated first, but that analyzer's own
    // diagnostic span covers the ENTIRE using-directives region of the WHOLE document, preamble
    // included, starting at position 0 — RoslynCodeFixEngine.ComputeFixesAsync's existing "diagnostic
    // starts before user code, skip" guard (correctly built for diagnostics genuinely confined to the
    // invisible preamble) ends up discarding it too, since it can't tell "starts in the preamble,
    // stays there" apart from "starts in the preamble, but the fix is really about user code". A
    // real, narrow gap — noted in TODO.md rather than papered over with a misleading test.)
    [Fact]
    public async Task NotifyTextChanged_Should_OfferAnAddAccessibilityModifierFix_ViaAHiddenSeverityAnalyzerDiagnostic()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<ICodeFixInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CodeFixesChanged += (_, f) =>
        {
            if (f.Count > 0) tcs.TrySetResult(f);
        };

        service.NotifyTextChanged("class Foo\n{\n    void Bar() { }\n}\n");

        var fixes = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(fixes, f => f.Title.Contains("accessibility", StringComparison.OrdinalIgnoreCase));
    }

    // Live user report (2026-07-21, TODO.md #74): for
    //   var a = new List<long>();
    //   a.Add(2);
    //   a.Add(3);
    //   a.Add(4);
    // the lightbulb showed "Collection initialization can be simplified" on EVERY line instead of
    // just the declaration. Root-caused via a live probe against RoslynAnalyzerEngine directly:
    // UseCollectionInitializerAnalyzer (IDE0028) reports ONE primary diagnostic (Info severity) on
    // `new List<long>()` plus one Hidden, WellKnownDiagnosticTags.Unnecessary-tagged "companion"
    // diagnostic on EACH `a.Add(...)` line (purely a fading/graying hint for the editor) — all four
    // share the same ID, so RoslynCodeFixEngine's old per-diagnostic-instance loop registered the
    // identical fix once per line. RoslynCodeFixEngine.ComputeFixesAsync now skips the
    // Unnecessary-tagged companions within an ID group whenever a non-Unnecessary primary is also
    // present in that same group.
    [Fact]
    public async Task NotifyTextChanged_Should_OfferCollectionInitializerFix_OnlyOnTheDeclarationLine_NotOnEveryAddCall()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<ICodeFixInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CodeFixesChanged += (_, f) =>
        {
            if (f.Any(x => x.DiagnosticId == "IDE0028")) tcs.TrySetResult(f);
        };

        service.NotifyTextChanged("var a = new List<long>();\na.Add(2);\na.Add(3);\na.Add(4);\n");

        var fixes = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        var collectionInitFixes = fixes.Where(f => f.DiagnosticId == "IDE0028").ToList();

        var onlyLine = Assert.Single(collectionInitFixes.Select(f => f.Line).Distinct());
        Assert.Equal(1, onlyLine);
    }

    // TODO.md #74 (сто сорок второй проход) — proves RoslynAnalyzerEngine's own-assembly reflection
    // extension (added alongside RunPad.Languages.CSharp.IntelliSense.Analyzers.
    // EmptyCatchClauseAnalyzer/DuplicateEnumValueAnalyzer) actually wires a hand-written analyzer all
    // the way through the SAME live pipeline the tests above prove for Microsoft's built-in ones — not
    // a separate/parallel path. See RunPadAnalyzersTests.cs for many more shape variations against the
    // analyzers directly (no AdhocWorkspace/preamble machinery needed there).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForRunPadsOwnEmptyCatchAnalyzer()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Any(x => x.Message.Contains("Empty catch", StringComparison.OrdinalIgnoreCase)))
                tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { void M() { } void N() { try { M(); } catch (System.Exception) { } } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Empty catch", StringComparison.OrdinalIgnoreCase));
    }

    // TODO.md #74 (сто сорок третий проход) — same end-to-end proof as above, for the newest
    // hand-written analyzer (NotImplementedExceptionAnalyzer). Info severity — still visible (only
    // Hidden is filtered out of the squiggle list, see ComputeDiagnosticsAndCodeFixesAsync).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForRunPadsOwnNotImplementedExceptionAnalyzer()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Any(x => x.Message.Contains("NotImplementedException", StringComparison.OrdinalIgnoreCase)))
                tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { void M() { throw new System.NotImplementedException(); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("NotImplementedException", StringComparison.OrdinalIgnoreCase));
    }

    // TODO.md #74 (сто сорок четвёртый проход) — same end-to-end proof as above, for
    // InvalidArgumentNullCheckAnalyzer.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForRunPadsOwnInvalidArgumentNullCheckAnalyzer()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Any(x => x.Message.Contains("is checked for null", StringComparison.OrdinalIgnoreCase)))
                tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { void M(object a, object b) { if (a == null) throw new System.ArgumentNullException(nameof(b)); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("is checked for null", StringComparison.OrdinalIgnoreCase));
    }

    // TODO.md #74 (сто сорок пятый проход) — same end-to-end proof as above, for
    // CultureSensitiveStringCompareAnalyzer.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForRunPadsOwnCultureSensitiveStringCompareAnalyzer()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Any(x => x.Message.Contains("current culture", StringComparison.OrdinalIgnoreCase)))
                tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { void M(string a, string b) { string.Compare(a, b); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("current culture", StringComparison.OrdinalIgnoreCase));
    }

    // TODO.md #74 (сто сорок шестой проход) — same end-to-end proof as above, for
    // UnusedPrivateMemberAnalyzer. Also the concrete proof behind that проход's own claim that neither
    // the compiler's CS0169 nor any built-in analyzer already covers this (see PROBE tests mentioned,
    // now removed, in that проход's TODO.md write-up).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForRunPadsOwnUnusedPrivateMemberAnalyzer()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Any(x => x.Message.Contains("is never used", StringComparison.OrdinalIgnoreCase)))
                tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { private int _unused; public void M() { } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("is never used", StringComparison.OrdinalIgnoreCase));
    }
}
