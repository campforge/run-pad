using System.Linq;
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

    // TODO.md #74 (сто сорок седьмой проход, batch) — one combined end-to-end proof for the whole
    // 12-analyzer batch added in that проход, rather than one test per analyzer (explicit user
    // direction: fewer tests per item once the backlog size made per-item depth impractical). Each
    // analyzer's own unit-level positive/negative cases live in RunPadAnalyzersTests.cs; this only
    // confirms RoslynAnalyzerEngine's reflection discovery still finds every new class and merges
    // their diagnostics into the same live squiggle pipeline.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 3) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(uint x, string s) { " +
            "if (x < 0) { } " +
            "try { } finally { } " +
            "s.ToString(); " +
            "} }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("never be less than zero", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics, d => d.Message.Contains("finally", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics, d => d.Message.Contains("ToString", StringComparison.OrdinalIgnoreCase));
    }

    // TODO.md #74 (сто сорок восьмой проход, batch) — same rationale as the проход 147 combined test
    // above: one end-to-end proof for the whole 10-analyzer batch instead of one per analyzer.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheSecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int Foo => Foo; public void M(bool flag) { if (flag == true) { } } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("recurse forever", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics, d => d.Message.Contains("redundant", StringComparison.OrdinalIgnoreCase));
    }

    // TODO.md #74 (сто сорок девятый проход, batch) — same rationale as the проход 147/148 combined
    // tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(string s) { s.StartsWith(\"x\"); string acc = \"\"; " +
            "for (int i = 0; i < 3; i++) { acc += i.ToString(); } } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("current culture", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics, d => d.Message.Contains("StringBuilder", StringComparison.OrdinalIgnoreCase));
    }

    // TODO.md #74 (сто пятидесятый проход, batch) — same rationale as the проход 147/148/149 combined
    // tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(double x) { var a = new int[0]; if (x == double.NaN) { } } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Array.Empty", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics, d => d.Message.Contains("NaN", StringComparison.OrdinalIgnoreCase));
    }

    // TODO.md #74 (сто пятьдесят первый проход, batch) — same rationale as the проход 147-150
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(string s) { s.ToUpper(); if (s == null || s == \"\") { } } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("ToUpperInvariant", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics, d => d.Message.Contains("IsNullOrEmpty", StringComparison.OrdinalIgnoreCase));
    }

    // TODO.md #74 (сто пятьдесят второй проход, batch) — same rationale as the проход 147-151
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheSixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public void M(int x, Dictionary<string, int> dict, string key) { " +
            "var abs = x < 0 ? -x : x; var v = dict.ContainsKey(key) ? dict[key] : 0; } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Math.Abs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics, d => d.Message.Contains("TryGetValue", StringComparison.OrdinalIgnoreCase));
    }

    // TODO.md #74 (сто пятьдесят третий проход, batch) — same rationale as the проход 147-152
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheSeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "enum Color { Red, Green, Blue } class C { public void M(int a, int b, Color c) { " +
            "double result = a / b; switch (c) { case Color.Red: break; } } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("truncated", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics, d => d.Message.Contains("does not handle", StringComparison.OrdinalIgnoreCase));
    }

    // TODO.md #74 (сто пятьдесят четвёртый проход, batch) — same rationale as the проход 147-153
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { private static readonly int Max = 5; public void M(int a, string s) { " +
            "var x = \"n: \" + a.ToString(); if (s.Length < 0) { } } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("redundant", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics, d => d.Message.Contains("never be negative", StringComparison.OrdinalIgnoreCase));
    }

    // TODO.md #74 (сто пятьдесят пятый проход, batch) — same rationale as the проход 147-154 combined
    // tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { " +
            "var m = xs.OrderBy(x => x).First(); var f = xs.Where(x => x > 0).FirstOrDefault(); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("MinBy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics, d => d.Message.Contains("simplified to", StringComparison.OrdinalIgnoreCase));
    }

    // TODO.md #74 (сто пятьдесят шестой проход, batch) — same rationale as the проход 147-155
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheTenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { " +
            "var y = xs.Where(x => x > 0).Where(x => x < 10); var z = xs.OrderBy(x => x).OrderBy(x => -x); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("combined", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics, d => d.Message.Contains("starts over", StringComparison.OrdinalIgnoreCase));
    }

    // TODO.md #74 (сто пятьдесят седьмой проход, batch) — same rationale as the проход 147-156
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheEleventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs, int item, List<string> ys, string s) { " +
            "var y = xs.Concat(new[] { item }); var b = ys.FirstOrDefault(x => x == s) != null; } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Append", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("FirstOrDefault", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто пятьдесят восьмой проход, batch) — same rationale as the проход 147-157
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheTwelfthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; class C { public void M(List<int> xs) { " +
            "var s = xs.Select(x => x * 2).Sum(); var y = xs.OrderBy(x => x).Reverse(); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Sum(...)", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("OrderByDescending", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто пятьдесят девятый проход, batch) — same rationale as the проход 147-158
    // combined tests above; covers both brand-new analyzers and the extended coverage of earlier ones.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheThirteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; using System.Text; " +
            "class C { public void M(List<int> xs, StringBuilder sb, int n) { " +
            "var a = xs.Select(x => x); sb.Append(n.ToString()); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("identity projection", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("StringBuilder.Append", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто шестидесятый проход, batch) — same rationale as the проход 147-159 combined
    // tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheFourteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(int n, char ch) { " +
            "var a = System.Convert.ToInt32(n); var b = char.ToUpper(ch); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Convert.ToInt32", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("char.ToUpper", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто шестьдесят первый проход, batch) — same rationale as the проход 147-160
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheFifteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; " +
            "class C { public void M(List<int> xs) { " +
            "var a = xs.Skip(0); var b = xs.Take(0); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Skip(0)", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Take(0)", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто шестьдесят второй проход, batch) — same rationale as the проход 147-161
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheSixteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(object a, object b) { " +
            "var s = string.Format(\"no placeholders\"); var eq = a.Equals(null); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("string.Format", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("NullReferenceException", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто шестьдесят третий проход, batch) — same rationale as the проход 147-162
    // combined tests above; bigger batch (7 new analyzers + 1 extension) per explicit user request.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheSeventeenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; " +
            "class C { public void M(IEnumerable<int> xs) { " +
            "var a = xs.Reverse().Reverse(); var b = xs.Distinct().Distinct(); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Reverse().Reverse()", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Distinct().Distinct()", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто шестьдесят четвёртый проход, batch) — same rationale as the проход 147-163
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheEighteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(string s, object obj) { " +
            "var a = s.Trim().Length == 0; var b = obj.GetType().Name == \"Foo\"; } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("IsNullOrWhiteSpace", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("GetType().Name", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто шестьдесят пятый проход, batch) — same rationale as the проход 147-164
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheNineteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; " +
            "enum Color { Red, Green } " +
            "class C { public void M(string s, int n) { " +
            "var a = System.Enum.Parse(typeof(Color), s); var b = n.ToString(\"G\"); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Enum.Parse<T>", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("ToString(\"G\")", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто шестьдесят шестой проход, batch) — same rationale as the проход 147-165
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheTwentiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(object x, string s) { " +
            "var a = x.Equals(x); var b = double.Parse(s); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("itself", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Double.Parse", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто шестьдесят седьмой проход, batch) — same rationale as the проход 147-166
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheTwentyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; " +
            "class C { public void M(List<int> xs, int item) { " +
            "var a = xs.Contains(item, EqualityComparer<int>.Default); " +
            "var b = xs.GroupBy(x => x % 2).Select(g => g.Key); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("EqualityComparer<T>.Default", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Select(key).Distinct()", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто шестьдесят восьмой проход, batch) — same rationale as the проход 147-167
    // combined tests above; minimum-10-analyzer batch per explicit user request.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheTwentySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; " +
            "class C { public void M(List<int> xs, int item, string s) { " +
            "var a = xs.ToList().Contains(item); var b = s.ToCharArray()[0]; } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("materializes the whole sequence", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("ToCharArray()", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто шестьдесят девятый проход, batch) — same rationale as the проход 147-168
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheTwentyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; using System.Threading.Tasks; " +
            "class C { public async Task M(List<int> xs) { " +
            "var a = xs.OrderBy(x => x.ToString()).ThenBy(x => x.ToString()); await Task.Delay(0); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("same key", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Task.Yield()", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто семидесятый проход, batch) — same rationale as the проход 147-169 combined
    // tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheTwentyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(object x, string s) { " +
            "var a = x is int || x is long; var b = s != null && s.Length > 0; } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("is A or B", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("IsNullOrEmpty", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто семьдесят первый проход, batch) — same rationale as the проход 147-170 combined
    // tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheTwentyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; " +
            "class C { public void M(int? x, int y, Dictionary<string, int> dict) { " +
            "var a = x.HasValue && x.Value == y; var b = dict.Keys.Count; } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("HasValue", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Keys.Count", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто семьдесят второй проход, batch) — same rationale as the проход 147-171 combined
    // tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheTwentySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(double x, char c) { " +
            "var a = System.Math.Round(x, 0); var b = char.IsLetter(c) || char.IsDigit(c); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Math.Round", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("IsLetterOrDigit", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто семьдесят третий проход, batch) — same rationale as the проход 147-172 combined
    // tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheTwentySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(object x, object y) { " +
            "var a = string.Format(\"{0}\", x); var b = !(x == y); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("string interpolation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics, d => d.Message.Contains("!=", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто семьдесят четвёртый проход, batch) — same rationale as the проход 147-173
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheTwentyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M() { System.GC.Collect(); } " +
            "public bool N(object x) { return object.Equals(x, null); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("GC.Collect", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("is null", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто семьдесят пятый проход, batch) — same rationale as the проход 147-174 combined
    // tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheTwentyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public bool M(bool b) => !!b; " +
            "public bool N(string a, string b) => a.ToLowerInvariant() == b.ToLowerInvariant(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Negating twice", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("OrdinalIgnoreCase", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто семьдесят шестой проход, batch) — same rationale as the проход 147-175 combined
    // tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheThirtiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M(int x) => x - x; " +
            "public int N() => System.Threading.Tasks.Task.FromResult(1).Result; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always evaluates to zero", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("wraps and immediately unwraps", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто семьдесят седьмой проход, batch) — same rationale as the проход 147-176
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheThirtyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(string x) { if (x != null) { x.ToString(); } } " +
            "public void N(System.Collections.Generic.IEnumerable<int> xs) { for (int i = 0; i < xs.Count(); i++) { } } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("x?.Member", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("O(n^2)", StringComparison.Ordinal));
    }
}
