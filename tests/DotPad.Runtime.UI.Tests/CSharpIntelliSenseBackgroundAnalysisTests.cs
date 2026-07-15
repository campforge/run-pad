using DotPad.Contracts.IntelliSense;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// ANALYSIS.md TODO #56: ComputeDiagnosticsAsync/ComputeCodeFixesAsync used to be two independent
// methods, each calling model.GetDiagnostics(...) itself — merged into one
// (ComputeDiagnosticsAndCodeFixesAsync) that computes diagnostics once and reuses them for both.
// These tests exercise the real background-analysis path (NotifyTextChanged) end-to-end to make
// sure both events still fire correctly off the shared computation.
public class CSharpIntelliSenseBackgroundAnalysisTests
{
    // Windows CI (GH Actions windows-latest) observed NotifyTextChanged_Should_MarkUnusedVariableFix_
    // AsWarningSeverity below time out at the old 10s budget while the identically-shaped "missing
    // using" tests in this same class passed in ~1s right next to it — this exercises real Roslyn MEF
    // codefix discovery plus (for CS0168) a full dataflow/unused-variable analysis pass, not just
    // symbol binding, and GH Actions' windows-latest runners are well documented to run .NET CPU-bound
    // work several times slower than the ubuntu-latest ones this suite is normally developed against
    // (weaker CPU allocation + Windows Defender real-time scanning of freshly-JITted/loaded Roslyn
    // assemblies) — this ran in ~2s locally on Linux with no flakiness across dozens of full-suite
    // runs, so there's no evidence of an actual hang, just insufficient headroom for a loaded/slow CI
    // machine. Shared by all three WaitAsync calls below so they stay consistent.
    private static readonly TimeSpan BackgroundAnalysisTimeout = TimeSpan.FromSeconds(30);

    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseDiagnosticsChanged_ForBrokenCode()
    {
        using var service = CreateService();
        IReadOnlyList<IDiagnosticInfo>? diagnostics = null;
        service.DiagnosticsChanged += (_, d) => diagnostics = d;

        // Undefined symbol - a real CS0103.
        service.NotifyTextChanged("Frobnicate();");

        for (var i = 0; i < 150 && diagnostics is null; i++)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.NotNull(diagnostics);
        Assert.NotEmpty(diagnostics);
    }

    [Fact]
    public async Task NotifyTextChanged_Should_RaiseCodeFixesChanged_ForAMissingUsing()
    {
        using var service = CreateService();

        var tcs = new TaskCompletionSource<IReadOnlyList<ICodeFixInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);

        service.CodeFixesChanged += (_, f) =>
        {
            if (f.Count > 0)
                tcs.TrySetResult(f);
        };

        // Regex isn't in the implicit-usings preamble, so this is CS0103 fixable by adding
        // `using System.Text.RegularExpressions;` — a real Roslyn CSharpAddImportCodeFixProvider fix.
        service.NotifyTextChanged("var m = Regex.Match(\"a\", \"a\");");

        var codeFixes = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(codeFixes, f => f.UsingsToAdd.Contains("System.Text.RegularExpressions"));
    }

    // TODO.md #75: ICodeFixInfo.Severity used to not exist at all — RoslynCodeFixEngine discarded
    // Diagnostic.Severity when building each fix, so the lightbulb margin had no way to color an
    // error-fixing action differently from a warning-fixing one. CS0103 (undefined "Regex") is a
    // real compiler ERROR, so the "add using" fix for it must carry Error through.
    [Fact]
    public async Task NotifyTextChanged_Should_MarkMissingUsingFix_AsErrorSeverity()
    {
        using var service = CreateService();

        var tcs = new TaskCompletionSource<IReadOnlyList<ICodeFixInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CodeFixesChanged += (_, f) =>
        {
            if (f.Count > 0)
                tcs.TrySetResult(f);
        };

        service.NotifyTextChanged("var m = Regex.Match(\"a\", \"a\");");

        var codeFixes = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        var fix = codeFixes.First(f => f.UsingsToAdd.Contains("System.Text.RegularExpressions"));
        Assert.Equal(DiagnosticSeverity.Error, fix.Severity);
    }

    // Same idea, opposite severity: CS0168 ("variable declared but never used") is a compiler
    // WARNING, not an error — Microsoft's own CSharpRemoveUnusedVariableCodeFixProvider (composed
    // via MEF like every other fix here) offers to remove it. Proves Severity isn't just always
    // Error (which the Regex test alone couldn't rule out).
    [Fact]
    public async Task NotifyTextChanged_Should_MarkUnusedVariableFix_AsWarningSeverity()
    {
        using var service = CreateService();

        var tcs = new TaskCompletionSource<IReadOnlyList<ICodeFixInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CodeFixesChanged += (_, f) =>
        {
            if (f.Count > 0)
                tcs.TrySetResult(f);
        };

        service.NotifyTextChanged("int unused;\n");

        var codeFixes = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        Assert.Contains(codeFixes, f => f.Severity == DiagnosticSeverity.Warning);
    }

    // Investigated as a SUSPECTED (not confirmed) contributor to the Windows-CI-only timeout on the
    // test above, after the 10s->30s budget bump alone didn't fix it: NotifyTextChanged's background
    // Task.Run was fire-and-forget — Dispose() called Cancel() but never waited for that Task.Run to
    // actually finish before returning. DiagnosticsChanged fires from INSIDE that same background
    // pass *before* the codefix half runs (see ComputeDiagnosticsAndCodeFixesAsync) — measured at a
    // real ~950ms gap between the two events for "Frobnicate()" below — so any caller that disposes
    // its service right after DiagnosticsChanged (like NotifyTextChanged_Should_RaiseDiagnosticsChanged_
    // ForBrokenCode above) could in principle leave that codefix computation still running on a
    // ThreadPool thread after Dispose() returns, invisible to the test framework and competing for
    // CPU/ThreadPool capacity with whatever test runs next.
    //
    // Caveat, found via this test's own doctrinal revert: reverting Dispose() to the old Cancel()-only
    // behavior did NOT make this test fail, even across repeated runs — Roslyn's own AddImport search
    // apparently checks the CancellationToken frequently enough on its own that the ~950ms window
    // above collapses to near-nothing once Cancel() fires, at least under the low contention of a
    // single test running alone. That doesn't rule this out as a real contributor on a genuinely
    // starved ThreadPool (many tests' worth of leaked background work queued at once on a
    // CPU-constrained CI runner is a different failure mode than "one cancellation checked in
    // isolation"), but it does mean this fix is not proven, only plausible-and-safe. Kept as a
    // correctness improvement regardless (Dispose waiting for its own background work before
    // releasing _workspace is more correct on its own merits — no caller should have to know about
    // this race to use the class safely), and this test still guards a real, permanently-useful
    // contract: no CodeFixesChanged after Dispose() returns, full stop.
    [Fact]
    public async Task Dispose_Should_PreventAnyLaterCodeFixesChangedEvent_EvenIfBackgroundAnalysisWasStillRunning()
    {
        var service = CreateService();
        var diagnosticsSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = false;
        var codeFixesFiredAfterDispose = false;

        service.DiagnosticsChanged += (_, _) => diagnosticsSeen.TrySetResult();
        service.CodeFixesChanged += (_, _) =>
        {
            if (disposed) codeFixesFiredAfterDispose = true;
        };

        service.NotifyTextChanged("Frobnicate();");
        await diagnosticsSeen.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);

        service.Dispose();
        disposed = true;

        // Give any leaked background work a real chance to fire before concluding it didn't.
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.False(codeFixesFiredAfterDispose);
    }

    // ANALYSIS.md TODO #56: SyncDocument's check-then-act on _lastCode/the AdhocWorkspace's
    // CurrentSolution was unsynchronized, even though completion/signature-help/quick-info/
    // background-analysis each call it from their own concurrent Task.Run. A genuine race is timing-
    // dependent and hard to force deterministically, so this is a smoke test, not a precise
    // reproduction: many concurrent calls with different code snapshots hitting the shared workspace
    // at once, past the point where the old unsynchronized version would very plausibly corrupt
    // shared state (throwing, or desyncing _lastCode from CurrentSolution).
    [Fact]
    public async Task GetCompletionsAsync_Should_NotThrow_WhenCalledConcurrently_WithDifferentCodeSnapshots()
    {
        using var service = CreateService();

        var tasks = Enumerable.Range(0, 20).Select(i =>
        {
            var code = $"var x{i} = {i};\nx{i}.";
            return Task.Run(async () =>
            {
                await service.GetCompletionsAsync(code, code.Length,
                    CompletionTrigger.Insertion('.'), TestContext.Current.CancellationToken);
                service.NotifyTextChanged(code);
            });
        });

        await Task.WhenAll(tasks);
    }

    // ANALYSIS.md TODO #56: MetadataReference.CreateFromFile (PE-header + XML-doc parsing) used to
    // run again for every one of the ~167 default BCL reference assemblies on every new tab, even
    // though references are immutable and safe to share across unrelated workspaces. ReferenceCache
    // is internal (InternalsVisibleTo, same pattern as CSharpReferenceService.IsManagedAssembly —
    // TODO.md #44) so this can verify the cache directly rather than only inferring it indirectly.
    [Fact]
    public void CreateReference_Should_ReturnTheSameCachedInstance_AcrossSeparateServiceInstances()
    {
        var referenceService = new CSharpReferenceService();
        var path = referenceService.GetDefault().First();

        using var service1 = new CSharpIntelliSenseService([path], NullLogger<CSharpIntelliSenseService>.Instance);
        Assert.True(CSharpIntelliSenseService.ReferenceCache.TryGetValue(path, out var afterFirst));

        using var service2 = new CSharpIntelliSenseService([path], NullLogger<CSharpIntelliSenseService>.Instance);
        Assert.True(CSharpIntelliSenseService.ReferenceCache.TryGetValue(path, out var afterSecond));

        Assert.Same(afterFirst, afterSecond);
    }
}
