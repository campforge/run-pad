using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Contracts.IntelliSense;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #74 "analyzer pipeline" (pass 103) — RoslynAnalyzerEngine, wired into
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
    // real CodeFixProvider, already discovered by RoslynCodeFixEngine since the pass before this
    // one) able to fire at all. Before this pass, ComputeDiagnosticsAndCodeFixesAsync only ever
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

    // TODO.md #74 (pass 142) — proves RoslynAnalyzerEngine's own-assembly reflection
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

    // TODO.md #74 (pass 143) — same end-to-end proof as above, for the newest
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

    // TODO.md #74 (pass 144) — same end-to-end proof as above, for
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

    // TODO.md #74 (pass 145) — same end-to-end proof as above, for
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

    // TODO.md #74 (pass 146) — same end-to-end proof as above, for
    // UnusedPrivateMemberAnalyzer. Also the concrete proof behind that pass's own claim that neither
    // the compiler's CS0169 nor any built-in analyzer already covers this (see PROBE tests mentioned,
    // now removed, in that pass's TODO.md write-up).
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

    // TODO.md #74 (pass 147, batch) — one combined end-to-end proof for the whole
    // 12-analyzer batch added in that pass, rather than one test per analyzer (explicit user
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

    // TODO.md #74 (pass 148, batch) — same rationale as the pass 147 combined test
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

    // TODO.md #74 (pass 149, batch) — same rationale as the pass 147/148 combined
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

    // TODO.md #74 (pass 150, batch) — same rationale as the pass 147/148/149 combined
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

    // TODO.md #74 (pass 151, batch) — same rationale as the pass 147-150
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

    // TODO.md #74 (pass 152, batch) — same rationale as the pass 147-151
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

    // TODO.md #74 (pass 153, batch) — same rationale as the pass 147-152
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

    // TODO.md #74 (pass 154, batch) — same rationale as the pass 147-153
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

    // TODO.md #74 (pass 155, batch) — same rationale as the pass 147-154 combined
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

    // TODO.md #74 (pass 156, batch) — same rationale as the pass 147-155
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

    // TODO.md #74 (pass 157, batch) — same rationale as the pass 147-156
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

    // TODO.md #74 (pass 158, batch) — same rationale as the pass 147-157
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

    // TODO.md #74 (pass 159, batch) — same rationale as the pass 147-158
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

    // TODO.md #74 (pass 160, batch) — same rationale as the pass 147-159 combined
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

    // TODO.md #74 (сто семьдесят восьмой проход, batch) — same rationale as the проход 147-177
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheThirtySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public bool M(object x) => typeof(string).IsInstanceOfType(x); " +
            "public bool N(int x) => System.Math.Abs(x) == 0; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("x is String", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Math.Abs", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто семьдесят девятый проход, batch) — same rationale as the проход 147-178
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheThirtyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public bool M(int x) => x == 1 || x == 2; " +
            "public bool N(int x, int y) => new[] { x }.Contains(y); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("is a or b", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("single-element array", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто восьмидесятый проход, batch) — same rationale as the проход 147-179 combined
    // tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheThirtyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(string s) { System.Console.Out.WriteLine(s); } " +
            "public string N(string x) => x.Trim().Trim(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Console.WriteLine", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("calls the same trim method twice", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто восемьдесят первый проход, batch) — same rationale as the проход 147-180
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheThirtyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public bool M(bool x) { if (x) return true; else return false; } " +
            "public void N(int[] arr) { System.Array.Sort(arr, 0, arr.Length); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("chooses between", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Array.Sort(array, comparer)", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто восемьдесят второй проход, batch) — same rationale as the проход 147-181
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheThirtySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => System.Threading.Thread.CurrentThread.ManagedThreadId; " +
            "public string N() => System.AppDomain.CurrentDomain.BaseDirectory; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Environment.CurrentManagedThreadId", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("AppContext.BaseDirectory", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто восемьдесят третий проход, batch) — same rationale as the проход 147-182
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheThirtySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M() { for (int i = 0; i < 10; i++) { var c = new System.Net.Http.HttpClient(); c.Dispose(); } } " +
            "public string N(string s) => System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(s)); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("socket exhaustion", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("GetString", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто восемьдесят четвёртый проход, batch) — same rationale as the проход 147-183
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheThirtyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public byte[] M(byte[] bytes) => System.Convert.FromBase64String(System.Convert.ToBase64String(bytes)); " +
            "public System.Guid N(System.Guid g) => System.Guid.Parse(g.ToString()); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Convert.FromBase64String", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Guid.Parse", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто восемьдесят пятый проход, batch) — same rationale as the проход 147-184
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheThirtyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Collections.Generic.IEnumerable<int> M(System.Collections.Generic.IEnumerable<int> xs) => xs.GroupBy(x => x % 2).Select(g => g.First()); " +
            "public System.Collections.Generic.IEnumerable<int> N(System.Collections.Generic.IEnumerable<int> xs) => xs.SelectMany(x => new[] { x }); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("DistinctBy", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("single-element array", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто восемьдесят шестой проход, batch) — same rationale as the проход 147-185
    // combined tests above; only one new analyzer this batch, so this proves it alone through the real
    // pipeline rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheFortiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M(System.Collections.Generic.List<int> xs) => xs.Count == 0 ? default : xs[0]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("FirstOrDefault", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто восемьдесят седьмой проход, batch) — same rationale as the проход 147-186
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheFortyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.DateTime M() => System.DateTime.Now.Date; " +
            "public bool N(System.DateTime x, System.DateTime y) => System.DateTime.Compare(x, y) == 0; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("DateTime.Today", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("DateTime.Compare", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто восемьдесят восьмой проход, batch) — same rationale as the проход 147-187
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheFortySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public bool M() => System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows); " +
            "public bool N() => System.IntPtr.Size == 8; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("OperatingSystem.IsWindows", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Environment.Is64BitProcess", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто восемьдесят девятый проход, batch) — same rationale as the проход 147-188
    // combined tests above.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryAnalyzerInTheFortyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "struct S { public int X; } class D { } " +
            "class C { public int M() => System.Runtime.InteropServices.Marshal.SizeOf(typeof(S)); " +
            "public object N() => System.Activator.CreateInstance(typeof(D)); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Marshal.SizeOf<T>", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Activator.CreateInstance<T>", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто девяностый проход, batch) — same rationale as the проход 147-189 combined
    // tests above; only one new analyzer this batch, so this proves it alone through the real pipeline
    // rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheFortyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public bool M(string x, string y) => string.Compare(x, y, System.StringComparison.Ordinal) == 0; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("string.Compare", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто девяносто первый проход, batch) — same rationale as the проход 147-190
    // combined tests above; one new analyzer this batch, so this proves both shapes it covers
    // (ToDictionary and ToLookup) through the real pipeline together.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryShapeInTheFortyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Collections.Generic.Dictionary<int, int> M(System.Collections.Generic.IEnumerable<int> xs) => xs.ToDictionary(x => x, x => x); " +
            "public System.Linq.ILookup<int, int> N(System.Collections.Generic.IEnumerable<int> xs) => xs.ToLookup(x => x, x => x); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("ToDictionary", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("ToLookup", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто девяносто второй проход, batch) — same rationale as the проход 147-191
    // combined tests above; one new analyzer this batch, so this proves both shapes it covers
    // through the real pipeline together.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryShapeInTheFortySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(System.Text.StringBuilder sb, string x) { sb.Append(x).AppendLine(); } " +
            "public void N(System.Text.StringBuilder sb) { sb.Append(System.Environment.NewLine); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("AppendLine(x)", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("AppendLine()", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто девяносто третий проход, batch) — same rationale as the проход 147-192
    // combined tests above; one new analyzer this batch, so this proves both shapes it covers
    // through the real pipeline together.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryShapeInTheFortySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(int[] arr) { System.Array.Clear(arr, 0, arr.Length); } " +
            "public int N(int[] arr, int value) => System.Array.IndexOf(arr, value, 0, arr.Length); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Array.Clear(array)", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Array.IndexOf(array, value)", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто девяносто четвёртый проход, batch) — same rationale as the проход 147-193
    // combined tests above; only one new analyzer this batch, so this proves it alone through the real
    // pipeline rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheFortyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { void M(int x, System.Threading.CancellationToken ct = default) { } " +
            "public void N() => M(5, System.Threading.CancellationToken.None); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("CancellationToken.None", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто девяносто пятый проход, batch) — same rationale as the проход 147-194 combined
    // tests above; only one new analyzer this batch, so this proves it alone through the real pipeline
    // rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheFortyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string M(System.Collections.Generic.IEnumerable<string> xs) => xs.Aggregate((a, b) => a + b); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("string.Concat", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто девяносто шестой проход, batch) — same rationale as the проход 147-195
    // combined tests above; one new analyzer this batch, so this proves both shapes it covers
    // through the real pipeline together.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseVisibleDiagnostics_ForEveryShapeInTheFiftiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count >= 2) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Collections.Generic.IEnumerable<int> M(System.Collections.Generic.Dictionary<int, int> dict) => dict.Select(kv => kv.Key); " +
            "public System.Collections.Generic.IEnumerable<int> N(System.Collections.Generic.Dictionary<int, int> dict) => dict.Select(kv => kv.Value); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains(".Keys", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains(".Values", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто девяносто седьмой проход, batch) — same rationale as the проход 147-196
    // combined tests above; only one new analyzer this batch, so this proves it alone through the real
    // pipeline rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheFiftyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Collections.Generic.Dictionary<int, int> M(System.Collections.Generic.Dictionary<int, int> dict) => dict.ToDictionary(kv => kv.Key, kv => kv.Value); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("new Dictionary<TKey, TValue>(dict)", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто девяносто восьмой проход, batch) — same rationale as the проход 147-197
    // combined tests above; only one new analyzer this batch, so this proves it alone through the real
    // pipeline rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheFiftySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string M(char[] chars) => string.Join(\"\", chars); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("new string(charArray)", StringComparison.Ordinal));
    }

    // TODO.md #74 (сто девяносто девятый проход, batch) — same rationale as the проход 147-198
    // combined tests above; only one new analyzer this batch, so this proves it alone through the real
    // pipeline rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheFiftyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string M(string dir, string file) => dir + \"\\\\\" + file; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Path.Combine", StringComparison.Ordinal));
    }

    // TODO.md #74 (двухсотый проход, batch) — same rationale as the проход 147-199 combined tests
    // above; only one new analyzer this batch, so this proves it alone through the real pipeline rather
    // than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheFiftyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.ReadOnlySpan<char> M(string s, int i, int len) => s.Substring(i, len).AsSpan(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("AsSpan", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести первый проход, batch) — same rationale as the проход 147-200 combined tests
    // above; only one new analyzer this batch, so this proves all three of its forms together through
    // the real pipeline rather than alongside a second, unrelated analyzer.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheFiftyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { " +
            "public bool M(System.Collections.Generic.List<int> xs) => xs.Any(x => x > 0); " +
            "public bool N(System.Collections.Generic.List<int> xs) => xs.All(x => x > 0); " +
            "public int O(System.Collections.Generic.List<int> xs) => xs.FirstOrDefault(x => x > 0); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Exists", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("TrueForAll", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Find", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести второй проход, batch) — same rationale as the проход 147-201 combined tests
    // above; only one new analyzer this batch, so this proves it alone through the real pipeline rather
    // than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheFiftySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string M(uint x) => x.ToString().PadLeft(4, '0'); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("\"Dn\"", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести третий проход, batch) — same rationale as the проход 147-202 combined tests
    // above; only one new analyzer this batch, so this proves it alone through the real pipeline rather
    // than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheFiftySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(System.Text.StringBuilder sb) { sb.Length = 0; } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("sb.Clear()", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести четвёртый проход, batch) — same rationale as the проход 147-203 combined
    // tests above; only one new analyzer this batch, so this proves all three of its forms together
    // through the real pipeline rather than alongside a second, unrelated analyzer.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheFiftyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { " +
            "public bool M(int[] xs) => xs.Any(x => x > 0); " +
            "public bool N(int[] xs) => xs.All(x => x > 0); " +
            "public int O(int[] xs) => xs.FirstOrDefault(x => x > 0); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Array.Exists", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Array.TrueForAll", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Array.Find", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести пятый проход, batch) — same rationale as the проход 147-204 combined tests
    // above; only one new analyzer this batch, so this proves it alone through the real pipeline rather
    // than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheFiftyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => new System.Random().Next(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Random.Shared.Next", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести шестой проход, batch) — same rationale as the проход 147-205 combined tests
    // above; only one new analyzer this batch, so this proves both of its forms together through the
    // real pipeline rather than alongside a second, unrelated analyzer.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSixtiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { " +
            "public int M(int x, int min, int max) => System.Math.Min(System.Math.Max(x, min), max); " +
            "public int N(int x, int min, int max) => System.Math.Max(System.Math.Min(x, max), min); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Math.Min(Math.Max", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Math.Max(Math.Min", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести седьмой проход, batch) — no new analyzer this batch, just a third shape
    // (Array.Reverse) added to the already-shipped RedundantExplicitFullRangeArrayOperationAnalyzer
    // (RPAD0181, проход 193) — this proves the new shape fires through the real pipeline.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSixtyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(int[] arr) { System.Array.Reverse(arr, 0, arr.Length); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Array.Reverse(array)", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести восьмой проход, batch) — same rationale as the проход 147-207 combined tests
    // above; only one new analyzer this batch, so this proves it alone through the real pipeline rather
    // than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSixtySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public bool M(string s) => bool.Parse(s.ToUpperInvariant()); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("bool.Parse", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести девятый проход, batch) — same rationale as the проход 147-208 combined tests
    // above; only one new analyzer this batch, so this proves it alone through the real pipeline rather
    // than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSixtyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(System.Collections.Generic.List<int> xs) { xs.Sort(); xs.Reverse(); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("b.CompareTo(a)", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести десятый проход, batch) — no new analyzer this batch, just six more method
    // names (Except/Intersect/Union/GroupBy/ThenBy/ThenByDescending) added to the already-shipped
    // RedundantDefaultComparerArgumentAnalyzer (RPAD0099, проход 167) — this proves the new shapes fire
    // through the real pipeline.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSixtyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public IEnumerable<int> M(int[] xs, int[] ys) => " +
            "xs.Except(ys, EqualityComparer<int>.Default); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Except", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести одиннадцатый проход, batch) — same rationale as the проход 147-209 combined
    // tests above; only one new analyzer this batch, so this proves it alone through the real pipeline
    // rather than alongside a second one. Deliberately fully-qualifies NumberStyles.Integer (rather than
    // relying on an extra `using System.Globalization;` the real preamble doesn't add) — unlike RPAD0099
    // (проход 210's lesson), this analyzer's styles-argument match is purely semantic, not syntactic, so
    // the fully-qualified form resolves correctly.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSixtyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M(string s) => int.Parse(s, System.Globalization.NumberStyles.Integer); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("NumberStyles.Integer", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести двенадцатый проход, batch) — same rationale as the проход 147-211 combined
    // tests above; only one new analyzer this batch, so this proves it alone through the real pipeline
    // rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSixtySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Collections.Generic.IEnumerable<string> M(string s) => s.Split(',').Where(x => x.Length > 0); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("RemoveEmptyEntries", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести тринадцатый проход, batch) — same rationale as the проход 147-212 combined
    // tests above; only one new analyzer this batch, so this proves it alone through the real pipeline
    // rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSixtySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Collections.Generic.IEnumerable<string> M(string s) => s.Split(',').Select(x => x.Trim()); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("TrimEntries", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести четырнадцатый проход, batch) — same rationale as the проход 147-213 combined
    // tests above; only one new analyzer this batch, so this proves it alone through the real pipeline
    // rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSixtyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "enum Color { Red, Green, Blue } class C { public bool M(Color a, Color b) => a.ToString() == b.ToString(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("a == b", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести пятнадцатый проход, batch) — same rationale as the проход 147-214 combined
    // tests above; only one new analyzer this batch, so this proves it alone through the real pipeline
    // rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSixtyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Collections.Generic.IEnumerable<int> M(int n) => Enumerable.Range(0, n).Select(i => 0); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Enumerable.Repeat", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести шестнадцатый проход, batch) — no new analyzer this batch, just four more
    // method names (ToDictionary/ToHashSet/MinBy/MaxBy) added to the already-shipped
    // RedundantDefaultComparerArgumentAnalyzer (RPAD0099, проход 167) — this proves one of the new
    // shapes fires through the real pipeline.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSeventiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public HashSet<int> M(int[] xs) => xs.ToHashSet(EqualityComparer<int>.Default); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("ToHashSet", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести семнадцатый проход, batch) — same rationale as the проход 147-216 combined
    // tests above; only one new analyzer this batch, so this proves all three of its forms together
    // through the real pipeline rather than alongside a second, unrelated analyzer.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSeventyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { " +
            "public int M(int[] xs) => xs.DefaultIfEmpty().First(); " +
            "public int N(int[] xs) => xs.DefaultIfEmpty().Last(); " +
            "public int O(int[] xs) => xs.DefaultIfEmpty().Single(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("FirstOrDefault", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("LastOrDefault", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("SingleOrDefault", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести восемнадцатый проход, batch) — same rationale as the проход 147-217 combined
    // tests above; only one new analyzer this batch, so this proves it alone through the real pipeline
    // rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSeventySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.TimeSpan M() => System.TimeSpan.FromSeconds(0); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("TimeSpan.Zero", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести девятнадцатый проход, batch) — same rationale as the проход 147-218 combined
    // tests above; only one new analyzer this batch, so this proves both of its forms together through
    // the real pipeline rather than alongside a second, unrelated analyzer.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSeventyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { " +
            "public int M(int[] xs) => xs.Aggregate((a, b) => Math.Max(a, b)); " +
            "public int N(int[] xs) => xs.Aggregate((a, b) => Math.Min(a, b)); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Max()", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Min()", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести двадцатый проход, batch) — same rationale as the проход 147-219 combined
    // tests above; only one new analyzer this batch, so this proves it alone through the real pipeline
    // rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSeventyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public bool M(int x, int y) => x.CompareTo(y) == 0; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("x == y", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести двадцать первый проход, batch) — no new analyzer this batch, just a fourth
    // shape (Array.Copy) added to the already-shipped RedundantExplicitFullRangeArrayOperationAnalyzer
    // (RPAD0181, проход 193) — this proves the new shape fires through the real pipeline.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSeventyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(int[] src, int[] dst) { System.Array.Copy(src, 0, dst, 0, src.Length); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Array.Copy(source, destination, length)", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести двадцать второй проход, batch) — same rationale as the проход 147-221
    // combined tests above; only one new analyzer this batch, so this proves both of its forms together
    // through the real pipeline rather than alongside a second, unrelated analyzer.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSeventySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { " +
            "public void M(System.Collections.Generic.List<int> xs) => xs.Sort(Comparer<int>.Default); " +
            "public int N(System.Collections.Generic.List<int> xs, int item) => xs.BinarySearch(item, Comparer<int>.Default); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Sort", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("BinarySearch", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести двадцать третий проход, batch) — same rationale as the проход 147-222
    // combined tests above; only one new analyzer this batch, so this proves it alone through the real
    // pipeline rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSeventySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M() { var sw = new System.Diagnostics.Stopwatch(); sw.Start(); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Stopwatch.StartNew()", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести двадцать четвёртый проход, batch) — same rationale as the проход 147-223
    // combined tests above; only one new analyzer this batch, so this proves it alone through the real
    // pipeline rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSeventyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string M(string a, string b, string c) => System.IO.Path.Combine(System.IO.Path.Combine(a, b), c); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Path.Combine", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести двадцать пятый проход, batch) — no new analyzer this batch, just three more
    // types (Version/TimeSpan/Guid) added to the already-shipped
    // PreferEqualityOperatorOverNumericCompareToAnalyzer (RPAD0205, проход 220) — this proves one of
    // the new shapes fires through the real pipeline.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheSeventyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public bool M(System.Guid g1, System.Guid g2) => g1.CompareTo(g2) == 0; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("x == y", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести двадцать шестой проход, batch) — same rationale as the проход 147-225
    // combined tests above; only one new analyzer this batch, so this proves it alone through the real
    // pipeline rather than alongside a second one.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheEightiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M(object a, object b) => a.GetHashCode() ^ b.GetHashCode(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("HashCode.Combine(a, b)", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести двадцать седьмой проход, batch) — larger batch at the user's request ("давай
    // продоолжим, но возьмем побольше"): one new analyzer (PreferCancellationTokenSourceConstructorOver
    // CancelAfterAnalyzer, RPAD0210) plus a new Array.Sort/Array.Reverse form on the already-shipped
    // PreferDescendingComparerOverSortThenReverseAnalyzer (RPAD0196, проход 209). Two solo combined
    // tests, one per change, since neither shares a snippet with the other.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheEightyFirstBatch_CancellationTokenSourceForm()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Threading.CancellationTokenSource M(System.TimeSpan delay) { " +
            "var cts = new System.Threading.CancellationTokenSource(); cts.CancelAfter(delay); return cts; } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("new CancellationTokenSource(delay)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheEightyFirstBatch_ArraySortReverseForm()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(int[] arr) { System.Array.Sort(arr); System.Array.Reverse(arr); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Array.Sort(arr, (a, b) => b.CompareTo(a))", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести двадцать восьмой проход, batch) — larger batch at the user's request
    // ("продолжай, бери сразу 10"): four independent, non-overlapping changes combined in one snippet
    // (PreferCombinedHasFlagAnalyzer/RPAD0211, PreferFirstOrDefaultOverElementAtOrDefaultZeroAnalyzer/
    // RPAD0212, PreferTrimOverTrimStartThenTrimEndAnalyzer/RPAD0213, and the new IsNullOrEmpty(Trim())
    // form on the already-shipped PreferIsNullOrWhiteSpaceAnalyzer/RPAD0084) plus a second Fact below for
    // the three Aggregate-shaped changes, kept separate so a false-positive double-match on one Aggregate
    // shape can't hide inside the other.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheEightySecondBatch_HasFlagTrimAndFirstOrDefaultForms()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; " +
            "[System.Flags] enum Perm { None = 0, Read = 1, Write = 2 } " +
            "class C { " +
            "public bool M1(Perm flags) => flags.HasFlag(Perm.Read) && flags.HasFlag(Perm.Write); " +
            "public int M2(int[] xs) => xs.ElementAtOrDefault(0); " +
            "public string M3(string s) => s.TrimStart().TrimEnd(); " +
            "public bool M4(string s) => string.IsNullOrEmpty(s.Trim()); " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("flags.HasFlag(A | B)", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("'ElementAtOrDefault(0)' can be replaced with 'FirstOrDefault()'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("can be replaced with 'Trim()'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("string.IsNullOrWhiteSpace(...)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheEightySecondBatch_AggregateForms()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; " +
            "class C { " +
            "public bool M1(int[] xs) => xs.Aggregate(true, (acc, x) => acc && x > 0); " +
            "public int M2(int[] xs) => xs.Aggregate(0, (acc, x) => acc + (x > 0 ? 1 : 0)); " +
            "public string M3(IEnumerable<string> xs) => xs.Aggregate(\"\", (acc, x) => acc + x); " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Use 'All(predicate)'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Use 'Count(predicate)'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Use 'string.Concat(xs)'", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести двадцать девятый проход, batch) — two new analyzers this batch
    // (PreferStringConcatOverJoinWithEmptySeparatorAnalyzer/RPAD0216,
    // PreferOperatorOverAddSubtractMethodAnalyzer/RPAD0217). A round-1 PROBE of 10 candidates showed the
    // other four (List/Array Where().ToList()/ToArray() and Select().ToList()/ToArray(), meant as
    // FindAll/ConvertAll suggestions) all collide with a built-in "Collection initialization can be
    // simplified" IDE rule on the exact ToList()/ToArray() span — dropped whole, same broad-overlap class
    // as IDE0090 in проходы 218/227. The two survivors combined in one snippet (5 Add/Subtract forms
    // plus the Join form — none share a span).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheEightyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; using System; " +
            "class C { " +
            "public string M1(List<string> xs) => string.Join(\"\", xs); " +
            "public DateTime M2(DateTime dt, TimeSpan ts) => dt.Add(ts); " +
            "public DateTime M3(DateTime dt, TimeSpan ts) => dt.Subtract(ts); " +
            "public TimeSpan M4(DateTime dt1, DateTime dt2) => dt1.Subtract(dt2); " +
            "public TimeSpan M5(TimeSpan ts1, TimeSpan ts2) => ts1.Add(ts2); " +
            "public TimeSpan M6(TimeSpan ts1, TimeSpan ts2) => ts1.Subtract(ts2); " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("string.Concat(xs)", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("'x.Add(y)' can be replaced with 'x + y'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("'x.Subtract(y)' can be replaced with 'x - y'", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести тридцатый проход, batch) — three new analyzers this batch
    // (PreferLastOverElementAtCountMinusOneAnalyzer/RPAD0218, RedundantConcatWithEmptyAnalyzer/RPAD0219,
    // PreferInstanceToStringOverConvertToStringForNumericAnalyzer/RPAD0220), combined in one snippet —
    // none share a span.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheEightyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; " +
            "class C { " +
            "public int M1(IEnumerable<int> xs) => xs.ElementAt(xs.Count() - 1); " +
            "public IEnumerable<int> M2(IEnumerable<int> xs) => xs.Concat(Enumerable.Empty<int>()); " +
            "public string M3(int x) => System.Convert.ToString(x); " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("can be replaced with 'xs.Last()'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("xs.Concat(Enumerable.Empty<T>())", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Convert.ToString(x)", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести тридцать первый проход, batch) — one new analyzer this batch
    // (RedundantTrimBeforeIntegralOrBoolParseAnalyzer/RPAD0221).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheEightyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M(string s) => int.Parse(s.Trim()); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Int32.Parse(s.Trim())' can be replaced with 'Int32.Parse(s)'", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести тридцать второй проход, batch) — one new analyzer
    // (PreferOrDefaultOverAnyCheckTernaryAnalyzer/RPAD0222) plus a new opposite-polarity form on
    // PreferFirstOrDefaultOverCountCheckTernaryAnalyzer/RPAD0171, combined in one snippet — no shared
    // span.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheEightySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; " +
            "class C { " +
            "public int M1(IEnumerable<int> xs) => xs.Any() ? xs.First() : default; " +
            "public int M2(List<int> xs) => xs.Count > 0 ? xs[0] : default; " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("xs.FirstOrDefault()", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("xs.Count > 0 ? xs[0] : default", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести тридцать третий проход, batch) — two new analyzers this batch
    // (PreferThrowIfNullOverIfNullCheckAnalyzer/RPAD0223, HasFlagWithZeroValueAnalyzer/RPAD0224),
    // combined in one snippet — no shared span.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheEightySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System; " +
            "[Flags] enum Perm { None = 0, Read = 1, Write = 2 } " +
            "class C { " +
            "public void M1(string x) { if (x == null) throw new ArgumentNullException(nameof(x)); } " +
            "public bool M2(Perm flags) => flags.HasFlag(Perm.None); " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("ArgumentNullException.ThrowIfNull(x)", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("always returns true", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести тридцать четвёртый проход, batch) — one new analyzer
    // (SelfArgumentMathMaxMinAnalyzer/RPAD0225) plus new forms on SelfEqualityCheckAnalyzer/RPAD0096
    // (HasFlag) and AlwaysFalseNonNegativeLengthComparisonAnalyzer/RPAD0051 (>=0 mirror), combined in one
    // snippet — no shared span.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheEightyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System; " +
            "[Flags] enum Perm { None = 0, Read = 1 } " +
            "class C { " +
            "public bool M1(Perm x) => x.HasFlag(x); " +
            "public bool M2(int[] xs) => xs.Length >= 0; " +
            "public int M3(int x) => Math.Max(x, x); " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Comparing a value to itself", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("always true", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Math.Max(x, x)", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести тридцать пятый проход, batch) — one new analyzer this batch
    // (SelfLogicalOrBitwiseOperatorAnalyzer/RPAD0226).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheEightyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public bool M(bool x) => x && x; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'x && x' always evaluates to x's own value", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести тридцать шестой проход, batch) — one new analyzer this batch
    // (LockOnNewObjectAnalyzer/RPAD0227) plus new typeof/string-literal forms on
    // AvoidLockOnThisAnalyzer/RPAD0014, combined in one snippet — no shared span.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheNinetiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int f; " +
            "public void M1() { lock (typeof(C)) { f++; } } " +
            "public void M2() { lock (new object()) { f++; } } " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Locking on a Type object", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("no two threads ever contend for the same lock", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести тридцать седьмой проход, batch) — two new analyzers this batch
    // (MonitorEnterWithoutTryFinallyAnalyzer/RPAD0228, DiscardedMonitorTryEnterResultAnalyzer/RPAD0229),
    // combined in one snippet — no shared span.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheNinetyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Threading; " +
            "class C { object o = new(); " +
            "void M1() { Monitor.Enter(o); DoWork(); Monitor.Exit(o); } " +
            "void M2() { Monitor.TryEnter(o); } " +
            "void DoWork() { } " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("immediately followed by a try/finally", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("returns false when the lock was NOT acquired", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести тридцать восьмой проход, batch) — one new analyzer this batch
    // (SemaphoreOrReaderWriterLockAcquireWithoutTryFinallyAnalyzer/RPAD0230).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheNinetySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Threading; " +
            "class C { SemaphoreSlim s = new(1); " +
            "void M() { s.Wait(); DoWork(); s.Release(); } " +
            "void DoWork() { } " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("SemaphoreSlim.Wait(...)", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести тридцать девятый проход, batch) — one new analyzer this batch
    // (NegativeCapacityArgumentAnalyzer/RPAD0231).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheNinetyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; " +
            "class C { public List<int> M() => new List<int>(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new List(-1)' always throws ArgumentOutOfRangeException", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести сороковой проход, batch) — two new analyzers this batch
    // (NegativeCountArgumentAnalyzer/RPAD0232, NegativeTimeoutArgumentAnalyzer/RPAD0233), combined in
    // one snippet — no shared span.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheNinetyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Threading.Tasks; " +
            "class C { " +
            "public void M1() => Enumerable.Repeat(0, -1); " +
            "public void M2() => Task.Delay(-2); " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Enumerable.Repeat(..., -1)' always throws", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("'Task.Delay(-2)' always throws", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести сорок первый проход, batch) — two new analyzers this batch
    // (InvalidRandomRangeArgumentAnalyzer/RPAD0234, SubstringOutOfRangeAnalyzer/RPAD0235), combined in
    // one snippet — no shared span.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheNinetyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System; " +
            "class C { " +
            "public int M1(Random r) => r.Next(10, 5); " +
            "public string M2() => \"abc\".Substring(1, 10); " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Next(10, 5)' always throws", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Substring(1, 10)' always throws", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести сорок второй проход, batch) — three new analyzers this batch
    // (NegativeElementAtIndexAnalyzer/RPAD0236, NegativeStringRepeatCountAnalyzer/RPAD0237,
    // InvalidConvertToIntegralBaseAnalyzer/RPAD0238), combined in one snippet — no shared span.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheNinetySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System; using System.Linq; using System.Collections.Generic; " +
            "class C { " +
            "public int M1(IEnumerable<int> xs) => xs.ElementAt(-1); " +
            "public string M2() => new string('x', -1); " +
            "public int M3(string s) => Convert.ToInt32(s, 3); " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("ElementAt(-1)' always throws", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("new string(c, -1)' always throws", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Convert.ToInt32(..., 3)' always throws", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести сорок третий проход, batch) — one new analyzer this batch
    // (AbsolutePathArgumentInPathCombineAnalyzer/RPAD0239).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheNinetySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.IO; " +
            "class C { public string M(string a) => Path.Combine(a, \"/etc/passwd\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("looks like a rooted path", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести сорок четвёртый проход, batch) — CultureSensitiveNumericParseAnalyzer/
    // RPAD0098 extended with DateTime.Parse/TimeSpan.Parse forms.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheNinetyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System; " +
            "class C { public DateTime M(string s) => DateTime.Parse(s); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'DateTime.Parse(...)' uses the current culture", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести сорок пятый проход, batch) — one new analyzer this batch
    // (NonPositiveChunkSizeAnalyzer/RPAD0240) plus a new PriorityQueue form on
    // NegativeCapacityArgumentAnalyzer/RPAD0231, combined in one snippet — no shared span.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheNinetyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; " +
            "class C { " +
            "public void M1(IEnumerable<int> xs) => xs.Chunk(0); " +
            "public PriorityQueue<int, int> M2() => new PriorityQueue<int, int>(-1); " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Chunk(0)' always throws", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("'new PriorityQueue(-1)' always throws", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести сорок шестой проход, batch) — InvalidRandomRangeArgumentAnalyzer/RPAD0234
    // extended with the single-argument Random.Next(maxValue) form.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System; " +
            "class C { public int M(Random r) => r.Next(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Next(-1)' always throws", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("cannot be negative", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести сорок седьмой проход, batch) — one new analyzer this batch
    // (DuplicateKeyInDictionaryInitializerAnalyzer/RPAD0241).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; " +
            "class C { public Dictionary<string, int> M() => new Dictionary<string, int> { { \"a\", 1 }, { \"a\", 2 } }; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("duplicates an earlier entry", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести сорок восьмой проход, batch) — one new analyzer this batch
    // (RedundantDuplicateValueInHashSetInitializerAnalyzer/RPAD0242).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; " +
            "class C { public HashSet<int> M() => new HashSet<int> { 1, 1 }; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("HashSet<T>.Add silently does nothing", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести сорок девятый проход, batch) — DuplicateKeyInDictionaryInitializerAnalyzer/
    // RPAD0241 extended with case-insensitive comparer detection.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System; using System.Collections.Generic; " +
            "class C { public Dictionary<string, int> M() => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { { \"a\", 1 }, { \"A\", 2 } }; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("duplicates an earlier entry", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести пятидесятый проход, batch) — one new analyzer this batch
    // (InsufficientCollectionCapacityAnalyzer/RPAD0243).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; " +
            "class C { public List<int> M() => new List<int>(2) { 1, 2, 3 }; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Capacity 2 is less than the 3 element(s)", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести пятьдесят первый проход, batch) — one new analyzer this batch
    // (IsDefinedOnFlagsEnumAnalyzer/RPAD0244).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System; [Flags] enum Perm { None = 0, Read = 1, Write = 2 } " +
            "class C { public bool M(Perm p) => Enum.IsDefined<Perm>(p); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("only matches a single named member", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести пятьдесят второй проход, batch) — one new analyzer this batch
    // (InvalidBase64LiteralLengthAnalyzer/RPAD0245).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System; class C { public byte[] M() => Convert.FromBase64String(\"abc\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("is not a valid Base64 string", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести пятьдесят третий проход, batch) — one new analyzer this batch
    // (InvalidGuidLiteralFormatAnalyzer/RPAD0246).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System; class C { public Guid M() => Guid.Parse(\"not-a-guid\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("doesn't match any of Guid's recognized formats", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести пятьдесят четвёртый проход, batch) — one new analyzer this batch
    // (InvalidVersionLiteralFormatAnalyzer/RPAD0247).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System; class C { public Version M() => Version.Parse(\"abc\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("major.minor[.build[.revision]]", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести пятьдесят пятый проход, batch) — one new analyzer this batch
    // (InvalidCultureNameLiteralAnalyzer/RPAD0248).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Globalization; " +
            "class C { public CultureInfo M() => CultureInfo.GetCultureInfo(\"!!!invalid!!!\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("is not a recognized culture name", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести пятьдесят шестой проход, batch) — one new analyzer this batch
    // (InvalidIPAddressLiteralAnalyzer/RPAD0249).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndTenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Net; class C { public IPAddress M() => IPAddress.Parse(\"not-an-ip\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("is not a valid IPv4 or IPv6 address", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести пятьдесят седьмой проход, batch) — one new analyzer this batch
    // (InvalidMailAddressLiteralAnalyzer/RPAD0250).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndEleventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Net.Mail; class C { public MailAddress M() => new MailAddress(\"not-an-email\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("is not a syntactically valid email address", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести пятьдесят восьмой проход, batch) — one new analyzer this batch
    // (MathAbsOfMinValueAnalyzer/RPAD0251).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndTwelfthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System; class C { public int M() => Math.Abs(int.MinValue); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws OverflowException", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести пятьдесят девятый проход, batch) — one new analyzer this batch
    // (PreferAppendOverStringBuilderInsertAtLengthAnalyzer/RPAD0252) plus a new Remove(0, Length) form
    // on PreferClearOverStringBuilderLengthZeroAnalyzer/RPAD0191, combined in one snippet — no shared
    // span.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndThirteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Text; " +
            "class C { " +
            "public void M1(StringBuilder sb, string x) => sb.Insert(sb.Length, x); " +
            "public void M2(StringBuilder sb) => sb.Remove(0, sb.Length); " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("can be replaced with 'sb.Append(value)'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("'sb.Remove(0, sb.Length)' can be replaced with 'sb.Clear()'", StringComparison.Ordinal));
    }

    // TODO.md #74 (двухсотшестидесятый проход, batch) — two new analyzers this batch
    // (InvalidBooleanLiteralAnalyzer/RPAD0253, InvalidEnumParseLiteralAnalyzer/RPAD0254), combined in
    // one snippet — no shared span.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFourteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "enum Color { Red, Green, Blue } " +
            "class C { " +
            "public bool M1() => bool.Parse(\"yes\"); " +
            "public Color M2() => Enum.Parse<Color>(\"Purple\"); " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("is not \"True\" or \"False\" (case-insensitive)", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("is not a defined member of 'Color'", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести шестьдесят первый проход, batch) — one new analyzer this batch
    // (InvalidUriLiteralAnalyzer/RPAD0255). The overlap with the built-in "'new' expression can be
    // simplified" (IDE0090, target-typed new) suggestion on the same `new Uri(...)` call is a different
    // reason on the same span — kept, established precedent (e.g. RPAD0090/Enum.Parse boxing concern,
    // проход 260).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFifteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Uri M() => new System.Uri(\"http://\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws UriFormatException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести шестьдесят второй проход, batch) — one new analyzer this batch
    // (InvalidHexStringLiteralAnalyzer/RPAD0256), plus InvalidBase64LiteralLengthAnalyzer/RPAD0245
    // rewritten to the "run the real validator" technique, combined in one snippet — no shared span.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSixteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { " +
            "public byte[] M1() => System.Convert.FromHexString(\"zz\"); " +
            "public byte[] M2() => System.Convert.FromBase64String(\"!!!!\"); " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Convert.FromHexString(...)' always throws FormatException", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Convert.FromBase64String(...)' always throws FormatException", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести шестьдесят третий проход, batch) — one new analyzer this batch
    // (InvalidXmlLiteralAnalyzer/RPAD0257). A parallel InvalidJsonLiteralAnalyzer candidate for
    // JsonDocument.Parse was PROBEd and dropped — the IDE's own built-in "JSON issue: ..." diagnostic
    // already covers the same span for the same reason (JsonDocument.Parse's parameter carries
    // [StringSyntax("Json")]); XDocument.Parse/XElement.Parse carry no such annotation, so no equivalent
    // built-in exists to collide with here.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSeventeenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Xml.Linq.XDocument M() => System.Xml.Linq.XDocument.Parse(\"not xml\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws XmlException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести шестьдесят четвёртый проход, batch) — one new analyzer this batch
    // (IntegralParseOverflowAnalyzer/RPAD0258).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndEighteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public byte M() => byte.Parse(\"300\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws OverflowException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести шестьдесят пятый проход, batch) — one new analyzer this batch
    // (InvalidTimeSpanFromDoubleArgumentAnalyzer/RPAD0259). The unrelated
    // PreferTimeSpanZeroOverFromXWithZeroAnalyzer (RPAD0203) firing on a separate `FromDays(0)` call in
    // the PROBE snippet was a different reason on a different call, not a collision.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndNineteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.TimeSpan M() => System.TimeSpan.FromDays(double.NaN); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести шестьдесят шестой проход, batch) — IntegralParseOverflowAnalyzer/RPAD0258
    // extended to also cover decimal.Parse/Convert.ToDecimal (no new analyzer this batch). A parallel
    // "mismatched string.Format placeholder count" candidate was PROBEd and dropped — the IDE's own
    // built-in "Format string contains invalid placeholder" diagnostic already covers the same span for
    // the same reason (string.Format's format-string parameter carries
    // [StringSyntax("CompositeFormat")]).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndTwentiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public decimal M() => decimal.Parse(\"79228162514264337593543950336\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws OverflowException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести шестьдесят седьмой проход, batch) — one new analyzer this batch
    // (DiscardedDisposableObjectCreationAnalyzer/RPAD0260).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndTwentyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M() { new System.IO.MemoryStream(); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Dispose() can never be called on it", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести шестьдесят восьмой проход, batch) — one new analyzer this batch
    // (RecordWithArrayMemberAnalyzer/RPAD0261).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndTwentySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("record Foo(int[] Data);");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("compares it by reference, not by value", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести шестьдесят девятый проход, batch) — RecordWithArrayMemberAnalyzer/RPAD0261
    // extended to also cover List<T>/Dictionary<TKey,TValue>/HashSet<T>/Queue<T>/Stack<T> (no new
    // analyzer this batch).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndTwentyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("record Foo(List<int> Data);");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("is a List — it doesn't override Equals/GetHashCode", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести семидесятый проход, batch) — one new analyzer this batch
    // (IEquatableWithoutObjectEqualsOverrideAnalyzer/RPAD0262).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndTwentyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "#nullable disable\n" +
            "class Foo : IEquatable<Foo> { " +
            "public int X; " +
            "public bool Equals(Foo other) => other != null && X == other.X; " +
            "public override int GetHashCode() => X; " +
            "}");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("doesn't override object.Equals(object)", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести семьдесят первый проход, batch) — one new analyzer this batch
    // (NegativeArrayResizeOrClearArgumentAnalyzer/RPAD0263).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndTwentyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public void M(int[] arr) => Array.Resize(ref arr, -1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws ArgumentOutOfRangeException at runtime — newSize cannot be negative", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести семьдесят второй проход, batch) — NegativeArrayResizeOrClearArgumentAnalyzer/
    // RPAD0263 extended to also cover the 5-arg Array.Copy overload (no new analyzer this batch).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndTwentySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(int[] src, int[] dst) => Array.Copy(src, -1, dst, 0, 1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Array.Copy(...)' always throws ArgumentOutOfRangeException at runtime — sourceIndex cannot be negative", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести семьдесят третий проход, batch) — NegativeArrayResizeOrClearArgumentAnalyzer/
    // RPAD0263 extended to also cover the 4-/5-arg Array.BinarySearch overloads (no new analyzer this
    // batch).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndTwentySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public int M(int[] arr) => Array.BinarySearch(arr, -1, 1, 2); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Array.BinarySearch(...)' always throws ArgumentOutOfRangeException at runtime — index cannot be negative", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести семьдесят четвёртый проход, batch) — one new analyzer this batch
    // (InvalidDateTimeConstructorArgumentAnalyzer/RPAD0264).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndTwentyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public DateTime M() => new DateTime(2023, 13, 1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести семьдесят пятый проход, batch) — InvalidDateTimeConstructorArgumentAnalyzer/
    // RPAD0264 extended to also cover DateOnly/TimeOnly constructors (no new analyzer this batch).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndTwentyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public DateOnly M() => new DateOnly(2023, 13, 1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new DateOnly(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести семьдесят шестой проход, batch) — one new analyzer this batch
    // (NegativeVersionComponentAnalyzer/RPAD0265).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndThirtiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public Version M() => new Version(-1, 0); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new Version(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести семьдесят седьмой проход, batch) — one new analyzer this batch
    // (InvalidGuidByteArrayLengthAnalyzer/RPAD0266).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndThirtyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public Guid M() => new Guid(new byte[] { 1, 2, 3 }); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new Guid(...)' always throws ArgumentException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести семьдесят восьмой проход, batch) — one new analyzer this batch
    // (InvalidIPAddressByteArrayLengthAnalyzer/RPAD0267).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndThirtySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public System.Net.IPAddress M() => new System.Net.IPAddress(new byte[] { 1, 2, 3 }); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new IPAddress(...)' always throws ArgumentException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести семьдесят девятый проход, batch) — one new analyzer this batch
    // (InvalidBitConverterArgumentAnalyzer/RPAD0268).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndThirtyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public int M() => BitConverter.ToInt32(new byte[4], -1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'BitConverter.ToInt32(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двухсотвосьмидесятый проход, batch) — one new analyzer this batch
    // (InvalidStringCharArrayConstructorArgumentAnalyzer/RPAD0269).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndThirtyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public string M() => new string(new char[3], 0, 5); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new string(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести восемьдесят первый проход, batch) — one new analyzer this batch
    // (InvalidEncodingGetStringArgumentAnalyzer/RPAD0270).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndThirtyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public string M() => System.Text.Encoding.UTF8.GetString(new byte[3], 0, 5); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Encoding.GetString(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести восемьдесят второй проход, batch) — NegativeArrayResizeOrClearArgumentAnalyzer/
    // RPAD0263 extended to also cover Array.ConstrainedCopy (no new analyzer this batch).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndThirtySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public void M(int[] src, int[] dst) => Array.ConstrainedCopy(src, -1, dst, 0, 1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Array.ConstrainedCopy(...)' always throws ArgumentOutOfRangeException at runtime — sourceIndex cannot be negative", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести восемьдесят третий проход, batch) — one new analyzer this batch
    // (InvalidArrayAsSpanOrAsMemoryArgumentAnalyzer/RPAD0271).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndThirtySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public Span<byte> M() => new byte[3].AsSpan(1, 5); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'AsSpan(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести восемьдесят четвёртый проход, batch) — one new analyzer this batch
    // (InvalidConvertToBase64StringArgumentAnalyzer/RPAD0272).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndThirtyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public string M() => Convert.ToBase64String(new byte[3], 0, 5); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.ToBase64String(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести восемьдесят пятый проход, batch) — one new analyzer this batch
    // (InvalidStringBuilderMaxCapacityArgumentAnalyzer/RPAD0273). Confirmed via PROBE that a negative
    // capacity is left entirely to the already-shipped NegativeCapacityArgumentAnalyzer (RPAD0231) — no
    // double-reporting on that case.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndThirtyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public System.Text.StringBuilder M() => new System.Text.StringBuilder(5, 3); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new StringBuilder(5, 3)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести восемьдесят шестой проход, batch) — one new analyzer this batch
    // (InvalidRegexOptionsCombinationAnalyzer/RPAD0274).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFortiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Text.RegularExpressions.Regex M() => new System.Text.RegularExpressions.Regex(\"a\", System.Text.RegularExpressions.RegexOptions.ECMAScript | System.Text.RegularExpressions.RegexOptions.RightToLeft); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new Regex(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести восемьдесят седьмой проход, batch) — one new analyzer this batch
    // (InvalidNumberStylesCombinationAnalyzer/RPAD0275).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFortyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => int.Parse(\"1\", System.Globalization.NumberStyles.AllowHexSpecifier | System.Globalization.NumberStyles.AllowDecimalPoint); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'int.Parse(...)' always throws ArgumentException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести восемьдесят восьмой проход, batch) — one new analyzer this batch
    // (InvalidDateTimeStylesCombinationAnalyzer/RPAD0276).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFortySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public DateTime M(string s) => DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AssumeUniversal); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'DateTime.Parse(...)' always throws ArgumentException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести восемьдесят девятый проход, batch) — one new analyzer this batch
    // (InvalidCompareOptionsCombinationAnalyzer/RPAD0277).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFortyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M(string a, string b) => System.Globalization.CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, System.Globalization.CompareOptions.Ordinal | System.Globalization.CompareOptions.IgnoreCase); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'CompareInfo.Compare(...)' always throws ArgumentException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести девяностый проход, batch) — extends InvalidConvertToIntegralBaseAnalyzer/RPAD0238
    // to also cover Convert.ToByte(string, int fromBase), missed in the original batch (проход 242).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFortyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public byte M(string s) => System.Convert.ToByte(s, 3); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.ToByte(..., 3)' always throws ArgumentException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести девяносто первый проход, batch) — one new analyzer this batch
    // (InvalidConvertToStringBaseAnalyzer/RPAD0278).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFortyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string M(int v) => System.Convert.ToString(v, 3); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.ToString(x, 3)' always throws ArgumentException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести девяносто второй проход, batch) — extends InvalidNumberStylesCombinationAnalyzer/
    // RPAD0275 to also cover System.Numerics.BigInteger.Parse.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFortySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Numerics.BigInteger M(string s) => System.Numerics.BigInteger.Parse(s, System.Globalization.NumberStyles.AllowHexSpecifier | System.Globalization.NumberStyles.AllowDecimalPoint); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'BigInteger.Parse(...)' always throws ArgumentException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести девяносто третий проход, batch) — extends InvalidDateTimeStylesCombinationAnalyzer/
    // RPAD0276 to also cover DateTimeOffset.Parse.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFortySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public DateTimeOffset M(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AssumeUniversal); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'DateTimeOffset.Parse(...)' always throws ArgumentException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести девяносто четвёртый проход, batch) — one new analyzer this batch
    // (InvalidRegexMatchTimeoutAnalyzer/RPAD0279).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFortyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Text.RegularExpressions.Regex M() => new System.Text.RegularExpressions.Regex(\"a\", System.Text.RegularExpressions.RegexOptions.None, System.TimeSpan.Zero); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new Regex(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести девяносто пятый проход, batch) — extends InvalidRegexMatchTimeoutAnalyzer/
    // RPAD0279 to also cover the static Regex.IsMatch/Match/Matches/Replace/Split overloads.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFortyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public bool M() => System.Text.RegularExpressions.Regex.IsMatch(\"a\", \"a\", System.Text.RegularExpressions.RegexOptions.None, System.TimeSpan.Zero); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Regex.IsMatch(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести девяносто шестой проход, batch) — one new analyzer this batch
    // (InvalidCancellationTokenSourceTimeSpanDelayAnalyzer/RPAD0280).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFiftiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Threading.CancellationTokenSource M() => new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(-5)); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new CancellationTokenSource(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести девяносто седьмой проход, batch) — extends
    // InvalidCancellationTokenSourceTimeSpanDelayAnalyzer/RPAD0280 to also cover Task.Delay(TimeSpan).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFiftyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Threading.Tasks.Task M() => System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(-5)); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Task.Delay(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести девяносто восьмой проход, batch) — one new analyzer this batch
    // (InvalidStringRemoveOrInsertArgumentAnalyzer/RPAD0281).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFiftySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string M() => \"hello\".Remove(1, 10); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'\"hello\".Remove(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (двести девяносто девятый проход, batch) — one new analyzer this batch
    // (StringLiteralIndexerOutOfRangeAnalyzer/RPAD0282).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFiftyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public char M() => \"hello\"[10]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'\"hello\"[10]' always throws IndexOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (трёхсотый проход, batch) — one new analyzer this batch
    // (ArrayLiteralIndexerOutOfRangeAnalyzer/RPAD0283).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFiftyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => new[] { 1, 2, 3 }[10]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Indexing this array literal with [10] always throws IndexOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста первый проход, batch) — one new analyzer this batch
    // (EnumerableRangeOverflowAnalyzer/RPAD0284).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFiftyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Collections.Generic.IEnumerable<int> M() => System.Linq.Enumerable.Range(int.MaxValue, 2); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws ArgumentOutOfRangeException at runtime — start + count - 1 overflows Int32.MaxValue", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста второй проход, batch) — extends NegativeElementAtIndexAnalyzer/RPAD0236 to
    // also catch a positive out-of-range index for an array-literal receiver.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFiftySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => new[] { 1, 2, 3 }.ElementAt(10); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'xs.ElementAt(10)' always throws ArgumentOutOfRangeException at runtime — the receiver has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFiftySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public char M() => System.Convert.ToChar(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.ToChar(-1)' always throws OverflowException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста четвёртый проход, batch) — extends ConvertToCharOverflowAnalyzer/RPAD0285 to
    // also cover the SByte/UInt32/UInt64 overloads.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFiftyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public char M() => System.Convert.ToChar((sbyte)-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.ToChar(-1)' always throws OverflowException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста пятый проход, batch) — one new analyzer this batch
    // (ConvertNumericNarrowingOverflowAnalyzer/RPAD0286).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndFiftyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public byte M() => System.Convert.ToByte(300); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.ToByte(300)' always throws OverflowException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста шестой проход, batch) — extends ConvertNumericNarrowingOverflowAnalyzer/
    // RPAD0286 to also cover a long source narrowing to int/uint/ulong.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSixtiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => System.Convert.ToInt32(5000000000L); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.ToInt32(5000000000)' always throws OverflowException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста седьмой проход, batch) — extends InvalidBitConverterArgumentAnalyzer/RPAD0268
    // to also cover the 3-argument BitConverter.ToString(byte[], int, int).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSixtyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string M() => System.BitConverter.ToString(new byte[4], 2, 3); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'BitConverter.ToString(...)' always throws ArgumentException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста восьмой проход, batch) — extends NegativeArrayResizeOrClearArgumentAnalyzer/
    // RPAD0263 to also cover Array.Reverse(array, index, length)/Array.Sort(array, index, length).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSixtySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(int[] a) => System.Array.Reverse(a, -1, 2); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Array.Reverse(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSixtyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(System.Collections.Generic.List<int> xs) => xs.Sort(-1, 2, null); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'xs.Sort(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста десятый проход, batch) — extends NegativeListIndexOrCountArgumentAnalyzer/
    // RPAD0287 to also cover List<T>.BinarySearch(index, count, item, comparer) and
    // List<T>.CopyTo(index, array, arrayIndex, count).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSixtyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(System.Collections.Generic.List<int> xs, int[] a) => xs.CopyTo(0, a, -1, 2); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'xs.CopyTo(...)' always throws ArgumentOutOfRangeException at runtime — arrayIndex cannot be negative", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста одиннадцатый проход, batch) — one new analyzer this batch
    // (InvalidStringBuilderRemoveOrInsertArgumentAnalyzer/RPAD0288).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSixtyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Text.StringBuilder M() => new System.Text.StringBuilder(\"hello\").Remove(10, 1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new StringBuilder(\"hello\").Remove(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста двенадцатый проход, batch) — one new analyzer this batch
    // (InvalidGuidParseExactFormatAnalyzer/RPAD0289).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSixtySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Guid M(string s) => System.Guid.ParseExact(s, \"Q\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Guid.ParseExact(..., \"Q\")' always throws FormatException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста тринадцатый проход, batch) — extends InvalidConvertToBase64StringArgumentAnalyzer/
    // RPAD0272 to also cover Convert.ToHexString(byte[], int, int).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSixtySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string M() => System.Convert.ToHexString(new byte[4], 2, 3); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.ToHexString(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста четырнадцатый проход, batch) — extends InvalidConvertToBase64StringArgumentAnalyzer/
    // RPAD0272 to also cover Convert.ToHexStringLower(byte[], int, int).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSixtyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string M() => System.Convert.ToHexStringLower(new byte[4], 2, 3); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.ToHexStringLower(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста пятнадцатый проход, batch) — extends InvalidRandomRangeArgumentAnalyzer/RPAD0234
    // to also cover Random.NextInt64.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSixtyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public long M(Random r) => r.NextInt64(5, 1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'NextInt64(5, 1)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста шестнадцатый проход, batch) — extends InvalidTimeSpanFromDoubleArgumentAnalyzer/
    // RPAD0259 to also cover TimeSpan.FromMicroseconds.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSeventiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.TimeSpan M() => System.TimeSpan.FromMicroseconds(double.NaN); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'TimeSpan.FromMicroseconds(NaN)' always throws at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста семнадцатый проход, batch) — one new analyzer this batch
    // (EmptyArrayLiteralFirstOrSingleOrLastAnalyzer/RPAD0290).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSeventyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => new int[0].First(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'xs.First(...)' always throws InvalidOperationException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста восемнадцатый проход, batch) — extends EmptyArrayLiteralFirstOrSingleOrLastAnalyzer/
    // RPAD0290 to also cover the no-seed Aggregate(func) overload.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSeventySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => new int[0].Aggregate((a, b) => a + b); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'xs.Aggregate(...)' always throws InvalidOperationException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста девятнадцатый проход, batch) — one new analyzer this batch
    // (EmptyQueueOrStackDequeueOrPopAnalyzer/RPAD0291).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSeventyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => new System.Collections.Generic.Queue<int>().Dequeue(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new Queue(...).Dequeue()' always throws InvalidOperationException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста двадцатый проход, batch) — extends EmptyQueueOrStackDequeueOrPopAnalyzer/
    // RPAD0291 to also cover PriorityQueue<TElement, TPriority> and LinkedList<T>.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSeventyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => new System.Collections.Generic.PriorityQueue<int, int>().Dequeue(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new PriorityQueue(...).Dequeue()' always throws InvalidOperationException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста двадцать первый проход, batch) — one new analyzer this batch
    // (NegativePadWidthArgumentAnalyzer/RPAD0292).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSeventyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string M(string s) => s.PadLeft(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'PadLeft(-1)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста двадцать второй проход, batch) — extends NegativeCapacityArgumentAnalyzer/
    // RPAD0231 to also cover SortedList<TKey, TValue>.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSeventySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Collections.Generic.SortedList<int, int> M() => new System.Collections.Generic.SortedList<int, int>(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new SortedList(-1)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста двадцать третий проход, batch) — one new analyzer this batch
    // (NegativeListCapacityAssignmentAnalyzer/RPAD0293).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSeventySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(System.Collections.Generic.List<int> xs) => xs.Capacity = -1; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'xs.Capacity = -1' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста двадцать четвёртый проход, batch) — extends NegativeListCapacityAssignmentAnalyzer/
    // RPAD0293 to also cover StringBuilder.Length and System.IO.MemoryStream.Position.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSeventyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M(System.Text.StringBuilder sb) => sb.Length = -1; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'sb.Length = -1' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста двадцать пятый проход, batch) — one new analyzer this batch
    // (InvalidArrayFillArgumentAnalyzer/RPAD0294).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndSeventyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M() => System.Array.Fill(new int[3], 0, -1, 1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Array.Fill(...)' always throws ArgumentOutOfRangeException at runtime — startIndex (-1) and count (1) don't fit within a 3-element array", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста двадцать шестой проход, batch) — one new analyzer this batch
    // (NegativeArrayPoolRentLengthAnalyzer/RPAD0295).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndEightiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Buffers; class C { public void M() => ArrayPool<int>.Shared.Rent(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ArrayPool<T>.Rent(-1)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста двадцать седьмой проход, batch) — one new analyzer this batch
    // (NegativeConsoleCursorPositionArgumentAnalyzer/RPAD0296).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndEightyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M() => System.Console.SetCursorPosition(-1, 0); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Console.SetCursorPosition(-1, 0)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста двадцать восьмой проход, batch) — extends InvalidArrayFillArgumentAnalyzer/
    // RPAD0294 to also cover Array.IndexOf<T>(array, value, startIndex, count).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndEightySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M() => System.Array.IndexOf(new int[3], 0, -1, 1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Array.IndexOf(...)' always throws ArgumentOutOfRangeException at runtime — startIndex (-1) and count (1) don't fit within a 3-element array", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста двадцать девятый проход, batch) — extends InvalidArrayFillArgumentAnalyzer/
    // RPAD0294 to also cover Array.FindIndex<T>(array, startIndex, count, match).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndEightyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M() => System.Array.FindIndex(new int[3], -1, 1, x => true); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Array.FindIndex(...)' always throws ArgumentOutOfRangeException at runtime — startIndex (-1) and count (1) don't fit within a 3-element array", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста тридцатый проход, batch) — one new analyzer this batch
    // (InvalidArrayLastIndexOfArgumentAnalyzer/RPAD0298).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndEightyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M() => System.Array.LastIndexOf(new int[3], 0, -1, 1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Array.LastIndexOf(...)' always throws ArgumentOutOfRangeException at runtime — startIndex (-1) and count (1) don't fit within a 3-element array", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста тридцать первый проход, batch) — extends NegativeListIndexOrCountArgumentAnalyzer/
    // RPAD0287 to also cover List<T>.IndexOf(item, index[, count])/LastIndexOf(item, index[, count]).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndEightyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public void M(List<int> xs) => xs.IndexOf(0, -1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'xs.IndexOf(...)' always throws ArgumentOutOfRangeException at runtime — index cannot be negative", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста тридцать второй проход, batch) — one new analyzer this batch
    // (InvalidStringIndexOfCharArgumentAnalyzer/RPAD0299).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndEightySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => \"abc\".IndexOf('a', -1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'\"abc\".IndexOf(...)' always throws ArgumentOutOfRangeException at runtime — the receiver has only 3 character(s)", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста тридцать третий проход, batch) — extends InvalidStringIndexOfCharArgumentAnalyzer/
    // RPAD0299 to also cover the string-value and StringComparison overloads.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndEightySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => \"abc\".IndexOf(\"a\", -1, System.StringComparison.Ordinal); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'\"abc\".IndexOf(...)' always throws ArgumentOutOfRangeException at runtime — the receiver has only 3 character(s)", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста тридцать четвёртый проход, batch) — one new analyzer this batch
    // (InvalidStringLastIndexOfArgumentAnalyzer/RPAD0300).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndEightyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => \"abc\".LastIndexOf('a', -1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'\"abc\".LastIndexOf(...)' always throws ArgumentOutOfRangeException at runtime — the receiver has only 3 character(s)", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста тридцать пятый проход, batch) — one new analyzer this batch
    // (InvalidStringJoinArgumentAnalyzer/RPAD0301).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndEightyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string M() => string.Join(\",\", new string[3], -1, 1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'string.Join(...)' always throws ArgumentOutOfRangeException at runtime — startIndex (-1) and count (1) don't fit within a 3-element array", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста тридцать шестой проход, batch) — one new analyzer this batch
    // (NegativeEnsureCapacityArgumentAnalyzer/RPAD0302).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndNinetiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public void M(List<int> xs) => xs.EnsureCapacity(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'xs.EnsureCapacity(-1)' always throws ArgumentOutOfRangeException at runtime — capacity cannot be negative", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста тридцать седьмой проход, batch) — extends NegativeEnsureCapacityArgumentAnalyzer/
    // RPAD0302 to also cover TrimExcess(int capacity).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndNinetyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public void M(Dictionary<int, int> d) => d.TrimExcess(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'d.TrimExcess(-1)' always throws ArgumentOutOfRangeException at runtime — capacity cannot be negative", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста тридцать восьмой проход, batch) — extends NegativeEnsureCapacityArgumentAnalyzer/
    // RPAD0302 to also cover PriorityQueue<TElement, TPriority>.EnsureCapacity.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndNinetySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public void M(PriorityQueue<int, int> pq) => pq.EnsureCapacity(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'pq.EnsureCapacity(-1)' always throws ArgumentOutOfRangeException at runtime — capacity cannot be negative", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста тридцать девятый проход, batch) — extends NegativeEnsureCapacityArgumentAnalyzer/
    // RPAD0302 to also cover System.Text.StringBuilder.EnsureCapacity.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndNinetyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Text; class C { public void M(StringBuilder sb) => sb.EnsureCapacity(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'sb.EnsureCapacity(-1)' always throws ArgumentOutOfRangeException at runtime — capacity cannot be negative", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста сороковой проход, batch) — extends EmptyArrayLiteralFirstOrSingleOrLastAnalyzer/
    // RPAD0290 to also cover Average/Min/Max/MinBy/MaxBy on non-nullable-value-type-returning overloads.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndNinetyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; class C { public int M() => new int[0].Min(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'xs.Min(...)' always throws InvalidOperationException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста сорок первый проход, batch) — two new analyzers this batch
    // (ListLiteralIndexerOutOfRangeAnalyzer/RPAD0303, NegativeRandomNumberGeneratorArgumentAnalyzer/RPAD0304).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndNinetyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }[5]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws ArgumentOutOfRangeException at runtime — this List<T> literal has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndNinetySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Security.Cryptography; class C { public byte[] M() => RandomNumberGenerator.GetBytes(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'RandomNumberGenerator.GetBytes(-1)' always throws ArgumentOutOfRangeException at runtime — count cannot be negative", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста сорок второй проход, batch) — one new analyzer this batch
    // (InvalidRandomNumberGeneratorGetInt32ArgumentAnalyzer/RPAD0306).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndNinetySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Security.Cryptography; class C { public int M() => RandomNumberGenerator.GetInt32(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'RandomNumberGenerator.GetInt32(-1)' always throws ArgumentOutOfRangeException at runtime — toExclusive must be positive", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста сорок третий проход, batch) — three new analyzers this batch
    // (NegativeStringSplitCountArgumentAnalyzer/RPAD0307, NegativeBitArrayOrArrayCreateInstanceLengthAnalyzer/
    // RPAD0308, InvalidSortedSetGetViewBetweenArgumentAnalyzer/RPAD0309) plus an extension of
    // NegativeCapacityArgumentAnalyzer/RPAD0231 to also cover System.IO.MemoryStream.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndNinetyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string[] M() => \"a,b\".Split(',', -1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'\"a,b\".Split(...)' always throws ArgumentOutOfRangeException at runtime — count cannot be negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheHundredAndNinetyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections; class C { public BitArray M() => new BitArray(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new BitArray(-1)' always throws ArgumentOutOfRangeException at runtime — length cannot be negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public SortedSet<int> M() => new SortedSet<int> { 1, 2, 3 }.GetViewBetween(5, 1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws ArgumentException at runtime — lowerValue must not be greater than upperValue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.IO; class C { public MemoryStream M() => new MemoryStream(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new MemoryStream(-1)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста сорок четвёртый проход, batch) — one new analyzer this batch
    // (InvalidConvertToCharArgumentAnalyzer/RPAD0310) plus an extension of
    // InvalidDateTimeConstructorArgumentAnalyzer/RPAD0264 to also cover TimeOnly's 5-arg overload.
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.TimeOnly M() => new System.TimeOnly(25, 0, 0, 0, 0); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new TimeOnly(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public char M() => System.Convert.ToChar(\"ab\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.ToChar(\"ab\")' always throws FormatException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста сорок пятый проход, batch) — two new analyzers this batch
    // (NegativeStringCompareSubstringArgumentAnalyzer/RPAD0311, DictionaryLiteralMissingKeyAnalyzer/RPAD0312).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => string.Compare(\"abc\", -1, \"def\", 0, 1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'string.Compare(...)' always throws ArgumentOutOfRangeException at runtime — indexA cannot be negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public string M() => new Dictionary<int, string> { { 1, \"a\" }, { 2, \"b\" } }[3]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws KeyNotFoundException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста сорок шестой проход, batch) — one new analyzer this batch
    // (DateTimeOrDateOnlyAddMethodOverflowAnalyzer/RPAD0313).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.DateTime M() => new System.DateTime(9999, 12, 31).AddDays(1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'DateTime.AddDays(1)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста сорок седьмой проход, batch) — two new analyzers this batch
    // (TimeSpanConstructorOverflowAnalyzer/RPAD0314, TimeSpanAddOrSubtractOperatorOverflowAnalyzer/RPAD0315).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.TimeSpan M() => new System.TimeSpan(int.MaxValue, 0, 0); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new TimeSpan(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.TimeSpan M() => System.TimeSpan.MaxValue + System.TimeSpan.FromDays(1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws OverflowException at runtime — the result is outside TimeSpan's representable range", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста сорок восьмой проход, batch) — four new analyzers this batch
    // (EnumerableSumOverflowAnalyzer/RPAD0316, EnumerableToDictionaryDuplicateKeyAnalyzer/RPAD0317,
    // CopyToDestinationTooSmallAnalyzer/RPAD0318, InvalidSemaphoreSlimConstructorArgumentAnalyzer/RPAD0319)
    // plus extensions of ConvertNumericNarrowingOverflowAnalyzer/RPAD0286 (double/float/decimal sources,
    // ToInt64 target) and ConvertToCharOverflowAnalyzer/RPAD0285 (double/float/decimal sources).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public int M() => System.Convert.ToInt32(1e20m); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws OverflowException at runtime — 100000000000000000000 is out of range for int", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndTenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public char M() => System.Convert.ToChar(70000.0); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.ToChar(70000)' always throws OverflowException at runtime — value must be between 0 and 65535", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndEleventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; class C { public int M() => new[] { int.MaxValue, 1 }.Sum(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'xs.Sum()' always throws OverflowException at runtime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndTwelfthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; using System.Collections.Generic; class C { public Dictionary<int, int> M() => new[] { 1, 2, 1 }.ToDictionary(x => x); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'xs.ToDictionary(x => x)' always throws ArgumentException at runtime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndThirteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.CopyTo(new int[2]); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws ArgumentException at runtime — the destination array has only 2 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFourteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Threading; class C { public SemaphoreSlim M() => new SemaphoreSlim(-1, 5); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new SemaphoreSlim(...)' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    // TODO.md #74 (триста сорок девятый проход, batch) — two new analyzers this batch
    // (EnumerableCastInvalidElementTypeAnalyzer/RPAD0320, BitArrayLiteralIndexerOutOfRangeAnalyzer/RPAD0321)
    // plus extensions of NegativeListCapacityAssignmentAnalyzer/RPAD0293 (Capacity < Count),
    // ConvertNumericNarrowingOverflowAnalyzer/RPAD0286 (ToDecimal target), and
    // ListLiteralIndexerOutOfRangeAnalyzer/RPAD0303 (RemoveRange/GetRange).
    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFifteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.Capacity = 1; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("cannot be less than the list's current Count (3)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSixteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public decimal M() => System.Convert.ToDecimal(double.MaxValue); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("is out of range for decimal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSeventeenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Linq; class C { public System.Collections.Generic.List<int> M() => new object[] { \"x\" }.Cast<int>().ToList(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws InvalidCastException at runtime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndEighteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections; class C { public bool M() => new BitArray(3)[5]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new BitArray(3)[5]' always throws ArgumentOutOfRangeException at runtime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndNineteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.RemoveRange(0, 10); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new List<int> { 1, 2, 3 }.RemoveRange(0, 10)' always throws ArgumentException at runtime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndTwentiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Threading; class C { public Semaphore M() => new Semaphore(10, 5); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("initialCount (10) cannot exceed maximumCount (5)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndTwentyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Threading; class C { public Barrier M() => new Barrier(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new Barrier(...)' always throws ArgumentOutOfRangeException at runtime — participantCount cannot be negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndTwentySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.ArraySegment<int> M() => new System.ArraySegment<int>(new int[3], 0, 10); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws ArgumentException at runtime — offset (0) and count (10) don't fit within a 3-element array", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndTwentyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Span<byte> M() => new System.Span<byte>(new byte[3], 0, 10); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new Span(...)' always throws ArgumentOutOfRangeException at runtime — start (0) and length (10) don't fit within a 3-element array", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndTwentyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public void M() => System.Array.Copy(new int[2], new int[5], 3); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("the source array has only 2 element(s), not enough for 3 element(s) starting at index 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndTwentyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string M() => char.ConvertFromUtf32(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("-1 is not a valid Unicode code point", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndTwentySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Text; class C { public Rune M() => new Rune((char)0xD800); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("a lone surrogate half is not a valid Rune", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndTwentySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public System.Index M() => new System.Index(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new Index(...)' always throws ArgumentOutOfRangeException at runtime — value cannot be negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndTwentyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Threading; class C { public ManualResetEventSlim M() => new ManualResetEventSlim(false, 2048); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("spinCount must be between 0 and 2047", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndTwentyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Threading; class C { public Timer M() => new Timer(_ => { }, null, -2, 100); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("dueTime (-2) must be -1 (Timeout.Infinite) or non-negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndThirtiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Threading; class C { public void M(Thread t) => t.Join(-2); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'t.Join(-2)' always throws ArgumentOutOfRangeException at runtime — millisecondsTimeout (-2) must be -1 (Timeout.Infinite) or non-negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndThirtyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Threading; class C { public bool M() => SpinWait.SpinUntil(() => true, -2); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("millisecondsTimeout (-2) must be -1 (Timeout.Infinite) or non-negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndThirtySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Concurrent; class C { public ConcurrentDictionary<int, int> M() => new ConcurrentDictionary<int, int>(0, 5); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("concurrencyLevel must be positive, or exactly -1 to use the default level", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndThirtyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Concurrent; class C { public BlockingCollection<int> M() => new BlockingCollection<int>(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("boundedCapacity must be positive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndThirtyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Threading; class C { public void M(SemaphoreSlim s) => s.Release(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'s.Release(-1)' always throws ArgumentOutOfRangeException at runtime — releaseCount must be positive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndThirtyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.IO; class C { public MemoryStream M() => new MemoryStream(new byte[3], 0, 10); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws ArgumentException at runtime — index (0) and count (10) don't fit within a 3-element array", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndThirtySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.IO; class C { public int M(MemoryStream ms) => ms.Read(new byte[2], 0, 10); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ms.Read(new byte[2], 0, 10)' always throws ArgumentOutOfRangeException at runtime — offset (0) and count (10) don't fit within a 2-element array", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndThirtySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.IO; class C { public void M(MemoryStream ms) => ms.CopyTo(new MemoryStream(), -1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ms.CopyTo(new MemoryStream(), -1)' always throws ArgumentOutOfRangeException at runtime — bufferSize must be positive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndThirtyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.IO; class C { public void M(MemoryStream ms) => ms.SetLength(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ms.SetLength(-1)' always throws ArgumentOutOfRangeException at runtime — length cannot be negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndThirtyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public string M() => string.Create(-5, 0, (span, x) => { }); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws ArgumentOutOfRangeException at runtime — length cannot be negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFortiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public decimal M() => new decimal(new int[] { 1, 2, 3 }); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new decimal(...)' always throws ArgumentException at runtime — the array is not a valid decimal bit representation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFortyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Text; class C { public void M(StringBuilder sb) => sb.CopyTo(0, new char[2], 0, 10); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'sb.CopyTo(0, new char[2], 0, 10)' always throws ArgumentException at runtime — the destination array has only 2 element(s), not enough for 10 element(s) starting at index 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFortySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public void M(List<int> list) => list.CopyTo(0, new int[2], 0, 10); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'list.CopyTo(0, new int[2], 0, 10)' always throws ArgumentException at runtime — the destination array has only 2 element(s), not enough for 10 element(s) starting at index 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFortyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }.BinarySearch(0, 10, 1, null); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new List<int> { 1, 2, 3 }.BinarySearch(0, 10, 1, null)' always throws ArgumentException at runtime — this List<T> literal has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFortyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public void M() => new List<int> { 1, 2, 3 }.InsertRange(10, new[] { 1 }); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new List<int> { 1, 2, 3 }.InsertRange(10, new[] { 1 })' always throws ArgumentOutOfRangeException at runtime — this List<T> literal has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFortyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Buffers; class C { public ArrayPool<int> M() => ArrayPool<int>.Create(-1, 10); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ArrayPool<int>.Create(-1, 10)' always throws ArgumentOutOfRangeException at runtime — maxArrayLength must be positive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFortySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Globalization; class C { public CultureInfo M() => new CultureInfo(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new CultureInfo(...)' always throws ArgumentOutOfRangeException at runtime — culture cannot be negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFortyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Text; class C { public int M(Encoding e) => e.GetByteCount(new char[3], 0, 10); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Encoding.GetByteCount(...)' always throws ArgumentOutOfRangeException at runtime — index (0) and count (10) don't fit within a 3-element array", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFortyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public int M() => new List<int> { 1, 2, 3 }.IndexOf(1, 0, 10); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new List<int> { 1, 2, 3 }.IndexOf(1, 0, 10)' always throws ArgumentOutOfRangeException at runtime — this List<T> literal has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFiftiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System.Collections.Generic; class C { public void M() => new Queue<int>(new[] { 1, 2, 3 }).CopyTo(new int[2], 0); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new Queue<int>(new[] { 1, 2, 3 }).CopyTo(new int[2], 0)' always throws ArgumentException at runtime — the destination array has only 2 element(s), not enough for 3 element(s) starting at index 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFiftyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System; class C { public TimeSpan M() => TimeSpan.MaxValue * 2; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'TimeSpan.MaxValue * 2' always throws OverflowException at runtime — the result is outside TimeSpan's representable range", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFiftySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "using System; class C { public DateTime M() => DateTime.MaxValue + TimeSpan.FromDays(1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'DateTime.MaxValue + TimeSpan.FromDays(1)' always throws ArgumentOutOfRangeException at runtime — the result is outside DateTime's representable range", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFiftyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public sbyte M() => System.Convert.ToSByte(200u); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.ToSByte(200)' always throws OverflowException at runtime — 200 is out of range for sbyte", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFiftyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged(
            "class C { public byte[] M() => System.Convert.FromHexString(\"abc\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.FromHexString(...)' always throws FormatException at runtime — 'abc' is not a valid hex string", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFiftyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public string M(int n) => n.ToString(\"Z\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'n.ToString(\"Z\")' always throws FormatException at runtime — 'Z' is not a recognized format specifier for int", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFiftySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public string M() => System.Guid.Empty.ToString(\"Z\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'System.Guid.Empty.ToString(\"Z\")' always throws FormatException at runtime — 'Z' is not a recognized format specifier for Guid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFiftySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Numerics; class C { public BigInteger M() => BigInteger.Parse(\"abc\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'BigInteger.Parse(...)' always throws FormatException at runtime — 'abc' is not a valid integer literal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFiftyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Net; class C { public IPEndPoint M() => new IPEndPoint(IPAddress.Loopback, 70000); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new IPEndPoint(...)' always throws ArgumentOutOfRangeException at runtime — port (70000) must be between 0 and 65535", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndFiftyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Numerics; class C { public BigInteger M() => BigInteger.Pow(2, -1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'BigInteger.Pow(2, -1)' always throws ArgumentOutOfRangeException at runtime — exponent cannot be negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSixtiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Threading.Channels; class C { public Channel<int> M() => Channel.CreateBounded<int>(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Channel.CreateBounded<int>(-1)' always throws ArgumentOutOfRangeException at runtime — capacity cannot be negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSixtyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Security.Cryptography; class C { public void M(Aes aes) => aes.KeySize = 100; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Aes.KeySize = 100' always throws CryptographicException at runtime — 100 is not a valid KeySize for Aes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSixtySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Threading.Tasks; class C { public void M(Task[] t) => Task.WaitAll(t, -2); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Task.WaitAll(t, -2)' always throws ArgumentOutOfRangeException at runtime — millisecondsTimeout (-2) must be -1 (Timeout.Infinite) or non-negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSixtyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Threading; class C { public void M(CountdownEvent ce) => ce.Signal(-1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ce.Signal(-1)' always throws ArgumentOutOfRangeException at runtime — the argument must be positive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSixtyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.IO; using System.IO.Compression; class C { public GZipStream M(Stream s) => new GZipStream(s, (CompressionLevel)99); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new GZipStream(...)' always throws ArgumentOutOfRangeException at runtime — 99 is not a valid CompressionLevel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSixtyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.IO; using System.IO.Compression; class C { public BrotliStream M(Stream s) => new BrotliStream(s, (CompressionLevel)99); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new BrotliStream(...)' always throws ArgumentException at runtime — 99 is not a valid CompressionLevel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSixtySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Text; class C { public Encoding M() => Encoding.GetEncoding(99999); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Encoding.GetEncoding(99999)' always throws ArgumentOutOfRangeException at runtime — codepage must be between 0 and 65535", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSixtySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Text; class C { public Encoding M() => Encoding.GetEncoding(\"invalid-name\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Encoding.GetEncoding(\"invalid-name\")' always throws ArgumentException at runtime — 'invalid-name' is not a registered encoding name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSixtyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public int M() => int.Parse(\"abc\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'int.Parse(...)' always throws FormatException at runtime — 'abc' is not a valid int", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSixtyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Threading; class C { public void M(Timer t) => t.Change(-2, 100); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'t.Change(-2, 100)' always throws ArgumentOutOfRangeException at runtime — dueTime (-2) must be -1 (Timeout.Infinite) or non-negative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSeventiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Collections.Immutable; class C { public int M() => ImmutableArray.Create(1, 2, 3)[10]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ImmutableArray.Create(1, 2, 3)[10]' always throws IndexOutOfRangeException at runtime — this ImmutableArray<T> literal has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSeventyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Collections.Immutable; class C { public ImmutableArray<int> M() => ImmutableArray.Create(1, 2, 3).RemoveAt(10); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ImmutableArray.Create(1, 2, 3).RemoveAt(10)' always throws ArgumentOutOfRangeException at runtime — this ImmutableArray<T> literal has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSeventySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Collections.Immutable; class C { public int M() => ImmutableList.Create(1, 2, 3)[10]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ImmutableList.Create(1, 2, 3)[10]' always throws ArgumentOutOfRangeException at runtime — this ImmutableList<T> literal has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSeventyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Collections.Immutable; class C { public ImmutableList<int> M() => ImmutableList.Create(1, 2, 3).Insert(10, 5); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ImmutableList.Create(1, 2, 3).Insert(10, 5)' always throws ArgumentOutOfRangeException at runtime — this ImmutableList<T> literal has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSeventyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public int M() => System.Array.BinarySearch(new int[] { 1, 2, 3 }, 0, 10, 1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'System.Array.BinarySearch(new int[] { 1, 2, 3 }, 0, 10, 1)' always throws ArgumentException at runtime — index (0) and length (10) don't fit within a 3-element array", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSeventyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Collections.Immutable; class C { public int M() => ImmutableList<int>.Empty.First(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'xs.First(...)' always throws InvalidOperationException at runtime — the receiver has no elements", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSeventySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Collections.Immutable; class C { public int M() => ImmutableStack<int>.Empty.Peek(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ImmutableStack.Empty.Peek()' always throws InvalidOperationException at runtime — the receiver is the known-empty singleton", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSeventySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Collections.Immutable; class C { public int M() => ImmutableDictionary<string, int>.Empty[\"k\"]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ImmutableDictionary.Empty[...]' always throws KeyNotFoundException at runtime — the receiver is the known-empty singleton", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSeventyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Collections.Immutable; class C { public int M() => default(ImmutableArray<int>)[0]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'default(ImmutableArray<...>)[...]' always throws NullReferenceException at runtime — a default/uninitialized ImmutableArray<T> wraps a null backing array", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndSeventyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public int M() => new[] { 1, 2, 3 }[^5]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Indexing this array literal with [^5] always throws IndexOutOfRangeException at runtime — it has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndEightiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public int M() => new System.Collections.Generic.List<int> { 1, 2, 3 }[^5]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws ArgumentOutOfRangeException at runtime — this List<T> literal has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndEightyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public char M() => \"abc\"[^5]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'\"abc\"[^5]' always throws IndexOutOfRangeException at runtime — the receiver has only 3 character(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndEightySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public int[] M() => new[] { 1, 2, 3 }[2..10]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new[] { 1, 2, 3 }[2..10]' always throws ArgumentOutOfRangeException at runtime — the receiver has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndEightyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Collections.Immutable; class C { public ImmutableStack<int> M() => ImmutableStack<int>.Empty.Pop(out var v); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ImmutableStack.Empty.Pop(out ...)' always throws InvalidOperationException at runtime — the receiver is the known-empty singleton", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndEightyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Collections.Immutable; class C { public ImmutableQueue<int> M() => ImmutableQueue<int>.Empty.Dequeue(out var v); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ImmutableQueue.Empty.Dequeue(out ...)' always throws InvalidOperationException at runtime — the receiver is the known-empty singleton", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndEightyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public int[] M() => new[] { 1, 2, 3 }[^5..]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new[] { 1, 2, 3 }[^5..]' always throws ArgumentOutOfRangeException at runtime — the receiver has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndEightySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public System.Collections.Generic.List<int> M() => new System.Collections.Generic.List<int> { 1, 2, 3 }[2..10]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws ArgumentException at runtime — the receiver has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndEightySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"{\\\"a\\\":1}\").RootElement.GetProperty(\"b\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'JsonDocument.Parse(\"{\\\"a\\\":1}\").RootElement.GetProperty(\"b\")' always throws KeyNotFoundException at runtime — the parsed JSON object has no property \"b\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndEightyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"[1,2,3]\").RootElement[10]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'JsonDocument.Parse(\"[1,2,3]\").RootElement[10]' always throws IndexOutOfRangeException at runtime — the parsed JSON array has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndEightyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"[1,2,3]\").RootElement.GetProperty(\"a\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'JsonDocument.Parse(\"[1,2,3]\").RootElement.GetProperty(\"a\")' always throws InvalidOperationException at runtime — the parsed JSON value is of kind Array, not Object", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndNinetiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Text.Json; class C { public JsonElement M() => JsonDocument.Parse(\"{\\\"a\\\":{\\\"c\\\":1}}\").RootElement.GetProperty(\"a\").GetProperty(\"b\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'JsonDocument.Parse(\"{\\\"a\\\":{\\\"c\\\":1}}\").RootElement.GetProperty(\"a\").GetProperty(\"b\")' always throws KeyNotFoundException at runtime — the parsed JSON object has no property \"b\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndNinetyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Text.Json; class C { public int M() => JsonDocument.Parse(\"{\\\"a\\\":\\\"hi\\\"}\").RootElement.GetProperty(\"a\").GetInt32(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'JsonDocument.Parse(\"{\\\"a\\\":\\\"hi\\\"}\").RootElement.GetProperty(\"a\").GetInt32()' always throws InvalidOperationException at runtime — The requested operation requires an element of type 'Number', but the target element has type 'String'.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndNinetySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Text.Json; class C { public System.Guid M() => JsonDocument.Parse(\"{\\\"a\\\":\\\"not-a-guid\\\"}\").RootElement.GetProperty(\"a\").GetGuid(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'JsonDocument.Parse(\"{\\\"a\\\":\\\"not-a-guid\\\"}\").RootElement.GetProperty(\"a\").GetGuid()' always throws FormatException at runtime — One of the identified items was in an invalid format.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndNinetyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public int M() => new System.Collections.Generic.SortedList<string, int> { { \"a\", 1 } }[\"b\"]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Indexing this SortedList<TKey,TValue> literal with this key always throws KeyNotFoundException at runtime — the key is not present in the initializer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndNinetyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public int M() => new System.Collections.Generic.SortedDictionary<string, int> { { \"a\", 1 } }[\"b\"]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("Indexing this SortedDictionary<TKey,TValue> literal with this key always throws KeyNotFoundException at runtime — the key is not present in the initializer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndNinetyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public System.DateTime M() => System.DateTime.Parse(\"hello\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'System.DateTime.Parse(\"hello\")' always throws FormatException at runtime — \"hello\" is not a valid DateTime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndNinetySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public System.TimeSpan M() => System.TimeSpan.Parse(\"hello\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'System.TimeSpan.Parse(\"hello\")' always throws FormatException at runtime — \"hello\" is not a valid TimeSpan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndNinetySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Net; class C { public IPNetwork M() => IPNetwork.Parse(\"hello\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'IPNetwork.Parse(...)' always throws FormatException at runtime — 'hello' is not a valid IP network in CIDR notation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndNinetyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Net; class C { public IPEndPoint M() => IPEndPoint.Parse(\"hello\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'IPEndPoint.Parse(...)' always throws FormatException at runtime — 'hello' is not a valid IP endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheTwoHundredAndNinetyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Net.NetworkInformation; class C { public PhysicalAddress M() => PhysicalAddress.Parse(\"hello\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'PhysicalAddress.Parse(...)' always throws FormatException at runtime — 'hello' is not a valid physical (MAC) address", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Text.Json.Nodes; class C { public JsonNode? M() => JsonNode.Parse(\"[1,2,3]\")[10]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'JsonNode.Parse(\"[1,2,3]\")[10]' always throws ArgumentOutOfRangeException at runtime — the parsed JSON array has only 3 element(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Numerics; class C { public float M() => new Vector2(1, 2)[2]; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new Vector2(1, 2)[2]' always throws ArgumentOutOfRangeException at runtime — Vector2 only has valid indices [0, 1]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Net.Http.Headers; class C { public MediaTypeHeaderValue M() => MediaTypeHeaderValue.Parse(\"\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'MediaTypeHeaderValue.Parse(...)' always throws FormatException at runtime — '' is not a valid MediaTypeHeaderValue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Globalization; class C { public RegionInfo M() => new RegionInfo(\"Narnia\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new RegionInfo(...)' always throws ArgumentException at runtime — 'Narnia' is not a recognized region name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Net.Mime; class C { public ContentType M() => new ContentType(\"no-slash-here\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new ContentType(...)' always throws FormatException at runtime — 'no-slash-here' is not a valid MIME content type", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Security.Cryptography; class C { public RSA M() => RSA.Create(1); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'RSA.Create(1)' always throws CryptographicException at runtime — 1 is not a valid key size for RSA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Runtime.Versioning; class C { public FrameworkName M() => new FrameworkName(\"NoCommaHere\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new FrameworkName(...)' always throws ArgumentException at runtime — 'NoCommaHere' is not a valid FrameworkName string", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Net.Mime; class C { public ContentDisposition M() => new ContentDisposition(\";;;\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new ContentDisposition(...)' always throws FormatException at runtime — ';;;' is not a valid content disposition string", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Globalization; class C { public string M() { var idn = new IdnMapping(); return idn.GetAscii(\"a..b\"); } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'idn.GetAscii(\"a..b\")' always throws ArgumentException at runtime — 'a..b' is not a valid IDN label or domain name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml.XPath; class C { public XPathExpression M() => XPathExpression.Compile(\"///bad[\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XPathExpression.Compile(...)' always throws XPathException at runtime — '///bad[' is not a syntactically valid XPath expression", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndTenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Numerics; using System.Globalization; class C { public Complex M() => Complex.Parse(\"garbage\", CultureInfo.InvariantCulture); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Complex.Parse(...)' always throws OverflowException at runtime — 'garbage' is not a valid Complex string (expected the \"<real; imaginary>\" format)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndEleventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Net; class C { public Cookie M() => new Cookie(\"bad=name\", \"value\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new Cookie(...)' always throws CookieException at runtime — 'bad=name' is not a valid cookie name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndTwelfthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public TimeZoneInfo M() => TimeZoneInfo.FindSystemTimeZoneById(\"Not/A/Real/Zone\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'TimeZoneInfo.FindSystemTimeZoneById(...)' always throws TimeZoneNotFoundException at runtime — 'Not/A/Real/Zone' is not a recognized time zone id on this system", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndThirteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public System.Net.Http.HttpMethod M() => new System.Net.Http.HttpMethod(\"GET POST\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new HttpMethod(...)' always throws FormatException at runtime — 'GET POST' is not a valid HTTP method token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFourteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Security.Cryptography.X509Certificates; class C { public X500DistinguishedName M() => new X500DistinguishedName(\"UNKNOWNKEY=test\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new X500DistinguishedName(...)' always throws CryptographicException at runtime — 'UNKNOWNKEY=test' is not a valid X.500 distinguished name string", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFifteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Data.Common; class C { public void M(DbConnectionStringBuilder b) => b.ConnectionString = \"=noKey\"; }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'DbConnectionStringBuilder.ConnectionString = \"=noKey\"' always throws ArgumentException at runtime — not a well-formed \"key=value\" connection string", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSixteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml; class C { public string M() => XmlConvert.VerifyNCName(\"has:colon\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XmlConvert.VerifyNCName(...)' always throws XmlException at runtime — 'has:colon' is not a valid XML NCName", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSeventeenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Reflection; class C { public AssemblyName M() => new AssemblyName(\"MyAssembly, Version=bad\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new AssemblyName(...)' always throws FileLoadException at runtime — 'MyAssembly, Version=bad' is not a valid assembly display name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndEighteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml.Linq; class C { public XName M() => XName.Get(\"{unterminated\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XName.Get(...)' always throws ArgumentException at runtime — '{unterminated' is not a valid expanded XML name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndNineteenthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Security.Cryptography; class C { public Oid M() => Oid.FromFriendlyName(\"not-a-real-friendly-name\", OidGroup.All); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Oid.FromFriendlyName(...)' always throws CryptographicException at runtime — 'not-a-real-friendly-name' is not a recognized OID friendly name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndTwentiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Data.SqlTypes; class C { public SqlDateTime M() => SqlDateTime.Parse(\"1700-01-01\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'SqlDateTime.Parse(...)' always throws SqlTypeException at runtime — '1700-01-01' is not a valid SqlDateTime literal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndTwentyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml; class C { public bool M() => XmlConvert.ToBoolean(\"True\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XmlConvert.ToBoolean(...)' always throws FormatException at runtime — 'True' is not a valid XML boolean literal (expected lowercase \"true\"/\"false\" or \"1\"/\"0\")", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndTwentySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml; class C { public System.TimeSpan M() => XmlConvert.ToTimeSpan(\"1.02:03:04\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XmlConvert.ToTimeSpan(...)' always throws FormatException at runtime — '1.02:03:04' is not a valid XSD duration literal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndTwentyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml; class C { public System.DateTimeOffset M() => XmlConvert.ToDateTimeOffset(\"1/1/2024\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XmlConvert.ToDateTimeOffset(...)' always throws FormatException at runtime — '1/1/2024' is not a valid XSD dateTime literal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndTwentyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.ImportFromPem(\"not a pem\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'rsa.ImportFromPem(...)' always throws ArgumentException at runtime — the text is not a valid PEM-encoded RSA key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndTwentyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Security.Cryptography.X509Certificates; class C { public X509Certificate2 M() => X509Certificate2.CreateFromPem(\"not a pem\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'X509Certificate2.CreateFromPem(...)' always throws CryptographicException at runtime — the text is not a valid PEM-encoded certificate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndTwentySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml; class C { public string M() => XmlConvert.VerifyNMTOKEN(\"has space\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XmlConvert.VerifyNMTOKEN(...)' always throws XmlException at runtime — 'has space' is not a valid XML NMTOKEN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndTwentySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Data.SqlTypes; class C { public SqlMoney M() => SqlMoney.Parse(\"922337203685477.5808\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'SqlMoney.Parse(...)' always throws OverflowException at runtime — '922337203685477.5808' is not a valid SqlMoney literal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndTwentyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Data.SqlTypes; class C { public SqlDecimal M() => SqlDecimal.Parse(\"999999999999999999999999999999999999999\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'SqlDecimal.Parse(...)' always throws OverflowException at runtime — '999999999999999999999999999999999999999' is not a valid SqlDecimal literal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndTwentyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Net.Mail; class C { public void M(MailAddressCollection c) => c.Add(\"a@b.com, not-an-email\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'c.Add(...)' always throws FormatException at runtime — 'a@b.com, not-an-email' is not a valid comma-separated email address list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndThirtiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml; class C { public string M() => XmlConvert.VerifyXmlChars(\"bad\\u0000char\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XmlConvert.VerifyXmlChars(...)' always throws XmlException at runtime — the text contains a character that is illegal in XML 1.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndThirtyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml.Linq; class C { public XProcessingInstruction M() => new XProcessingInstruction(\"1bad\", \"data\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new XProcessingInstruction(...)' always throws XmlException at runtime — '1bad' is not a valid processing instruction target", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndThirtySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Security.Cryptography; class C { public void M(ECDsa ec) => ec.ImportFromPem(\"not a pem\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ec.ImportFromPem(...)' always throws ArgumentException at runtime — the text is not a valid PEM-encoded EC key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndThirtyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Security.Cryptography; class C { public void M(DSA dsa) => dsa.ImportFromPem(\"not a pem\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'dsa.ImportFromPem(...)' always throws ArgumentException at runtime — the text is not a valid PEM-encoded DSA key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndThirtyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public void M() => new System.Guid(\"not-a-guid\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new Guid(...)' always throws FormatException at runtime — 'not-a-guid' doesn't match any of Guid's recognized formats", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndThirtyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Security.Cryptography; class C { public void M(RSA rsa) => rsa.ImportFromEncryptedPem(\"not a pem\", \"pw\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'rsa.ImportFromEncryptedPem(...)' always throws ArgumentException at runtime — the text is not a valid PEM-encoded (encrypted) RSA key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndThirtySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Security.Cryptography; class C { public void M(DSA dsa) => dsa.ImportFromEncryptedPem(\"not a pem\", \"pw\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'dsa.ImportFromEncryptedPem(...)' always throws ArgumentException at runtime — the text is not a valid PEM-encoded (encrypted) DSA key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndThirtySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Security.Cryptography; class C { public void M(ECDsa ec) => ec.ImportFromEncryptedPem(\"not a pem\", \"pw\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ec.ImportFromEncryptedPem(...)' always throws ArgumentException at runtime — the text is not a valid PEM-encoded (encrypted) EC key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndThirtyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Buffers.Text; class C { public byte[] M() => Base64Url.DecodeFromChars(\"not base64url!!\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Base64Url.DecodeFromChars(...)' always throws FormatException at runtime — 'not base64url!!' is not a valid URL-safe Base64 string", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndThirtyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml.Linq; class C { public XDocumentType M() => new XDocumentType(\"1bad\", null, null, null); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new XDocumentType(...)' always throws XmlException at runtime — '1bad' is not a valid XML Name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFortiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Security.Cryptography; class C { public void M() => PemEncoding.Find(\"not a pem\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'PemEncoding.Find(...)' always throws ArgumentException at runtime — the text does not contain a valid PEM-encoded structure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFortyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Data.SqlTypes; class C { public SqlBoolean M() => SqlBoolean.Parse(\"notabool\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'SqlBoolean.Parse(...)' always throws FormatException at runtime — 'notabool' is not a recognized SqlBoolean representation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFortySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml; class C { public string M() => XmlConvert.VerifyWhitespace(\"a\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XmlConvert.VerifyWhitespace(...)' always throws XmlException at runtime — 'a' contains a character that is not XML whitespace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFortyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml; class C { public string M() => XmlConvert.VerifyPublicId(\"<bad>\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XmlConvert.VerifyPublicId(...)' always throws XmlException at runtime — '<bad>' contains a character that is not valid in an XML PUBLIC ID literal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFortyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Data.SqlTypes; class C { public SqlGuid M() => SqlGuid.Parse(\"not-a-guid\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'SqlGuid.Parse(...)' always throws FormatException at runtime — 'not-a-guid' doesn't match any of SqlGuid's recognized formats", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFortyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml; class C { public System.Guid M() => XmlConvert.ToGuid(\"not-a-guid\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XmlConvert.ToGuid(...)' always throws FormatException at runtime — 'not-a-guid' doesn't match any of Guid's recognized formats", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFortySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml; class C { public System.DateTime M() => XmlConvert.ToDateTime(\"not-a-date\", XmlDateTimeSerializationMode.RoundtripKind); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XmlConvert.ToDateTime(...)' always throws FormatException at runtime — 'not-a-date' is not a valid XSD dateTime literal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFortySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml; class C { public void M() => new XmlDocument().LoadXml(\"not xml\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new XmlDocument().LoadXml(...)' always throws XmlException at runtime — this literal is not well-formed XML", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFortyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Diagnostics; class C { public ActivityTraceId M() => ActivityTraceId.CreateFromString(\"abc\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ActivityTraceId.CreateFromString(...)' always throws ArgumentOutOfRangeException at runtime — 'abc' is not 32 lowercase hex characters", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFortyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public System.TimeZoneInfo M() => System.TimeZoneInfo.FromSerializedString(\"abc123\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'TimeZoneInfo.FromSerializedString(...)' always throws SerializationException at runtime — the text is not a valid TimeZoneInfo serialized string", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFiftiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public System.Type M() => System.Type.GetType(\"System.String[[[\", true); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Type.GetType(...)' always throws ArgumentException at runtime — 'System.String[[[' is empty or has malformed generic-arity syntax", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFiftyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public System.Version M() => new System.Version(\"1.x\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new Version(...)' always throws FormatException at runtime — this literal doesn't have 2 to 4 dot-separated non-negative integer components", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFiftySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Diagnostics; class C { public ActivityContext M() => ActivityContext.Parse(\"not a traceparent\", null); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ActivityContext.Parse(...)' always throws ArgumentException at runtime — 'not a traceparent' is not a valid W3C traceparent header", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFiftyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public System.UriBuilder M() => new System.UriBuilder(\"http://bad host/\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new UriBuilder(...)' always throws UriFormatException at runtime — 'http://bad host/' is not a valid URI", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFiftyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public string M() => \"abc\\uD800def\".Normalize(); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'...Normalize(...)' always throws ArgumentException at runtime — this literal contains an unpaired UTF-16 surrogate character", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFiftyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.IO; class C { public string M() => Path.GetFullPath(\"abc\\0def\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Path.GetFullPath(...)' always throws ArgumentException at runtime — the path literal contains an embedded NUL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFiftySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml; class C { public char M() => XmlConvert.ToChar(\"ab\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XmlConvert.ToChar(...)' always throws FormatException at runtime — 'ab' is not exactly one character long", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFiftySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml; class C { public int M() => XmlConvert.ToInt32(\"abc\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XmlConvert.ToInt32(...)' always throws FormatException at runtime — 'abc' is not a valid XSD numeric literal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFiftyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Security.Cryptography; class C { public byte[] M(byte[] d) => CryptographicOperations.HashData(new HashAlgorithmName(\"bogus\"), d); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'CryptographicOperations.HashData(...)' always throws CryptographicException at runtime — 'bogus' is not a known hash algorithm name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndFiftyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System; using System.Globalization; class C { public DateTime M() => DateTime.ParseExact(\"not-a-date\", \"yyyy-MM-dd\", CultureInfo.InvariantCulture); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws FormatException at runtime — \"not-a-date\" does not match the format \"yyyy-MM-dd\" under CultureInfo.InvariantCulture", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSixtiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("class C { public int M() => System.Convert.ToInt32(\"abc\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.ToInt32(...)' always throws FormatException at runtime — 'abc' is not a valid int", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSixtyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Numerics; class C { public BigInteger M(BigInteger v, BigInteger e) => BigInteger.ModPow(v, e, 0); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("always throws DivideByZeroException at runtime — modulus cannot be zero", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSixtySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Globalization; class C { public CultureInfo M() => CultureInfo.GetCultureInfoByIetfLanguageTag(\"###\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'CultureInfo.GetCultureInfoByIetfLanguageTag(...)' always throws CultureNotFoundException at runtime — '###' is not a recognized culture name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSixtyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Xml.Linq; class C { public XElement M() => new XElement(\"bad name\", \"v\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new XElement(...)' always throws XmlException at runtime — 'bad name' is not a valid expanded XML name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSixtyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Data; class C { public void M() { var t = new DataTable(); var col = t.Columns.Add(\"E\"); col.Expression = \"1+\"; } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'DataColumn.Expression = ...' always throws SyntaxErrorException at runtime — '1+' is not a well-formed DataColumn expression", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSixtyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.IO; class C { public string M() => Path.GetRelativePath(\"\", \"/x\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Path.GetRelativePath(...)' always throws ArgumentException at runtime — the 'relativeTo' argument is empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSixtySixthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Reflection.Metadata; class C { public AssemblyNameInfo M() => AssemblyNameInfo.Parse(\"\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'AssemblyNameInfo.Parse(...)' always throws ArgumentException at runtime — '' is not a valid assembly display name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSixtySeventhBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Reflection.Metadata; class C { public TypeName M() => TypeName.Parse(\"System.Int32[\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'TypeName.Parse(...)' always throws ArgumentException at runtime — 'System.Int32[' is not a well-formed reflection type name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSixtyEighthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System; class C { public DateTime M() => Convert.ToDateTime(\"today\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'Convert.ToDateTime(...)' always throws FormatException at runtime — \"today\" is not a valid DateTime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSixtyNinthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Data.SqlTypes; class C { public SqlInt32 M() => SqlInt32.Parse(\"abc\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'SqlInt32.Parse(...)' always throws FormatException at runtime — 'abc' is not a valid SqlInt32 literal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSeventiethBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Net; class C { public WebProxy M() => new WebProxy(\"\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'new WebProxy(...)' always throws UriFormatException at runtime — '' is not a well-formed URI", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSeventyFirstBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System; using System.Numerics; class C { public string M(BigInteger b) => b.ToString(\"Z\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("is not a recognized format specifier for BigInteger", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSeventySecondBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System; using System.Xml; class C { public string M(DateTime d) => XmlConvert.ToString(d, \"Z\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'XmlConvert.ToString(...)' always throws FormatException at runtime — 'Z' is not a recognized format specifier for DateTime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSeventyThirdBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System; class C { public TimeZoneInfo M() => TimeZoneInfo.CreateCustomTimeZone(\"id\", TimeSpan.FromHours(15), \"d\", \"s\"); }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'TimeZoneInfo.CreateCustomTimeZone(...)' always throws ArgumentOutOfRangeException at runtime — baseUtcOffset must be within ±14:00:00 and a whole number of minutes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSeventyFourthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Globalization; class C { public void M() { new NumberFormatInfo().CurrencyPositivePattern = 4; } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'NumberFormatInfo.CurrencyPositivePattern = 4' always throws ArgumentOutOfRangeException at runtime — valid values are 0-3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseAVisibleDiagnostic_ForTheThreeHundredAndSeventyFifthBatch()
    {
        using var service = CreateService();
        var tcs = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnosticsChanged += (_, d) =>
        {
            if (d.Count > 0) tcs.TrySetResult(d);
        };

        service.NotifyTextChanged("using System.Globalization; class C { public void M() { new NumberFormatInfo().NumberGroupSizes = new[] { 3, 0, 2 }; } }");

        var diagnostics = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, d => d.Message.Contains("'NumberFormatInfo.NumberGroupSizes = ...' always throws ArgumentException at runtime — every element must be between 1 and 9, except the last element, which may also be 0", StringComparison.Ordinal));
    }
}
