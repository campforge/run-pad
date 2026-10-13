using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Contracts.IntelliSense;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// ComputeDiagnosticsAsync/ComputeCodeFixesAsync used to be two independent
// methods, each calling model.GetDiagnostics(...) itself — merged into one
// (ComputeDiagnosticsAndCodeFixesAsync) that computes diagnostics once and reuses them for both.
// These tests exercise the real background-analysis path (NotifyTextChanged) end-to-end to make
// sure both events still fire correctly off the shared computation.
public class CSharpIntelliSenseBackgroundAnalysisTests
{
    // NotifyTextChanged_Should_MarkUnusedVariableFix_AsWarningSeverity below was reproducibly timing
    // out on Windows CI (GH Actions windows-latest) only, while every sibling test in this class
    // passed quickly right next to it. Root cause, confirmed via logging added across several rounds
    // (see that test's own comment for the full story and the CI log evidence): Roslyn's
    // RemoveUnusedVariableCodeFixProvider reformats the blank line before the statement it deletes,
    // and that reformatting fell back to FormattingOptions.NewLine's default — Environment.NewLine,
    // i.e. "\r\n" on Windows — while CSharpIntelliSenseService's synthetic document is built entirely
    // with literal '\n'. The resulting edit's span landed 2 bytes earlier than expected, straddling
    // into the preamble just enough to trip RoslynCodeFixEngine's "unsafe preamble edit, discard the
    // whole fix" safety check — invisible on Linux/macOS, where Environment.NewLine already happens
    // to be "\n". Fixed by having CSharpIntelliSenseService force FormattingOptions.NewLine to "\n"
    // explicitly on its workspace. Kept the 30s budget (real headroom for a genuinely CPU-heavier
    // pass than the sibling tests, independent of the bug above) and the instrumentation on that test
    // — both are still useful defenses if a *different* cause ever produces a similar symptom.
    private static readonly TimeSpan BackgroundAnalysisTimeout = TimeSpan.FromSeconds(30);

    private static CSharpIntelliSenseService CreateService(ILogger<CSharpIntelliSenseService>? logger = null)
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, logger ?? NullLogger<CSharpIntelliSenseService>.Instance);
    }

    // Captures every log message regardless of level/IsEnabled — CSharpIntelliSenseService/
    // RoslynCodeFixEngine both log swallowed exceptions at Debug level (routine-looking, e.g. "this
    // fix provider doesn't apply here"), which NullLogger (used everywhere else in this file)
    // discards entirely. If a fix provider is genuinely throwing on Windows and nowhere else, this is
    // the only way any evidence of that would ever reach a CI log.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_messages)
                _messages.Add(exception is null ? $"[{logLevel}] {message}" : $"[{logLevel}] {message} :: {exception}");
        }
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
        const string code = "var m = Regex.Match(\"a\", \"a\");";

        var tcs = new TaskCompletionSource<IReadOnlyList<ICodeFixInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);

        service.CodeFixesChanged += (_, f) =>
        {
            if (f.Count > 0)
                tcs.TrySetResult(f);
        };

        // Regex isn't in the implicit-usings preamble, so this is CS0103 fixable by adding
        // `using System.Text.RegularExpressions;` — a real Roslyn CSharpAddImportCodeFixProvider fix.
        service.NotifyTextChanged(code);

        var codeFixes = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);

        // CodeFixesChanged's
        // own entries no longer carry UsingsToAdd (that's materialized on demand now, see
        // ICodeFixInfo's own doc comment) — materialize each candidate to find the one that actually
        // adds the expected using, same as a real click would.
        var materialized = await Task.WhenAll(codeFixes.Select(
            f => service.MaterializeFixAsync(code, f.Key, TestContext.Current.CancellationToken)));
        Assert.Contains(materialized, m => m?.UsingsToAdd.Contains("System.Text.RegularExpressions") == true);
    }

    // ICodeFixInfo.Severity used to not exist at all — RoslynCodeFixEngine discarded
    // Diagnostic.Severity when building each fix, so the lightbulb margin had no way to color an
    // error-fixing action differently from a warning-fixing one. CS0103 (undefined "Regex") is a
    // real compiler ERROR, so the "add using" fix for it must carry Error through.
    [Fact]
    public async Task NotifyTextChanged_Should_MarkMissingUsingFix_AsErrorSeverity()
    {
        using var service = CreateService();
        const string code = "var m = Regex.Match(\"a\", \"a\");";

        var tcs = new TaskCompletionSource<IReadOnlyList<ICodeFixInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CodeFixesChanged += (_, f) =>
        {
            if (f.Count > 0)
                tcs.TrySetResult(f);
        };

        service.NotifyTextChanged(code);

        var codeFixes = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);

        // Same "materialize to find the right one" adjustment as the test above.
        ICodeFixInfo? fix = null;
        foreach (var candidate in codeFixes)
        {
            var materialized = await service.MaterializeFixAsync(code, candidate.Key, TestContext.Current.CancellationToken);
            if (materialized?.UsingsToAdd.Contains("System.Text.RegularExpressions") == true) { fix = candidate; break; }
        }
        Assert.NotNull(fix);
        Assert.Equal(DiagnosticSeverity.Error, fix.Severity);
    }

    // Same idea, opposite severity: CS0168 ("variable declared but never used") is a compiler
    // WARNING, not an error — Microsoft's own CSharpRemoveUnusedVariableCodeFixProvider (composed
    // via MEF like every other fix here) offers to remove it. Proves Severity isn't just always
    // Error (which the Regex test alone couldn't rule out).
    //
    // Instrumented (see class doc comment) rather than a plain WaitAsync: this specific test is the
    // one reproducibly timing out on Windows CI with no local repro and no confirmed cause yet after
    // two rounds of investigation. Records every DiagnosticsChanged/CodeFixesChanged firing plus a
    // real (not Null) logger's captured messages, and reports all of it in the failure message if the
    // wait times out — so the next CI failure shows, at minimum: whether CS0168 was even detected at
    // all, how many CodeFixesChanged batches fired and their sizes, and any swallowed exception a fix
    // provider threw (RoslynCodeFixEngine logs those at Debug, which NullLogger normally discards).
    [Fact]
    public async Task NotifyTextChanged_Should_MarkUnusedVariableFix_AsWarningSeverity()
    {
        var logger = new CapturingLogger<CSharpIntelliSenseService>();
        using var service = CreateService(logger);

        var diagnosticsBatches = new List<IReadOnlyList<IDiagnosticInfo>>();
        var codeFixBatches = new List<IReadOnlyList<ICodeFixInfo>>();
        var tcs = new TaskCompletionSource<IReadOnlyList<ICodeFixInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);

        service.DiagnosticsChanged += (_, d) => { lock (diagnosticsBatches) diagnosticsBatches.Add(d); };
        service.CodeFixesChanged += (_, f) =>
        {
            lock (codeFixBatches) codeFixBatches.Add(f);
            if (f.Count > 0)
                tcs.TrySetResult(f);
        };

        service.NotifyTextChanged("int unused;\n");

        IReadOnlyList<ICodeFixInfo> codeFixes;
        try
        {
            codeFixes = await tcs.Task.WaitAsync(BackgroundAnalysisTimeout, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            var diagSummary = diagnosticsBatches.Count == 0
                ? "(none — DiagnosticsChanged never fired at all)"
                : string.Join(" | ", diagnosticsBatches.Select((d, i) =>
                    $"batch{i}=[{string.Join(", ", d.Select(x => $"{x.Severity}:{x.Message}"))}]"));
            var fixSummary = codeFixBatches.Count == 0
                ? "(none — CodeFixesChanged never fired at all)"
                : string.Join(" | ", codeFixBatches.Select((f, i) => $"batch{i}=count:{f.Count}"));
            var logSummary = logger.Messages.Count == 0 ? "(none)" : string.Join(" | ", logger.Messages);

            Assert.Fail(
                $"Timed out after {BackgroundAnalysisTimeout} waiting for a non-empty CodeFixesChanged. "
                + $"DiagnosticsChanged fired {diagnosticsBatches.Count} time(s): {diagSummary}. "
                + $"CodeFixesChanged fired {codeFixBatches.Count} time(s): {fixSummary}. "
                + $"Logger messages: {logSummary}");
            throw; // unreachable — Assert.Fail always throws
        }

        Assert.Contains(codeFixes, f => f.Severity == DiagnosticSeverity.Warning);
    }

    // Root cause of the timeout above, confirmed via the logging added in the two rounds above: on
    // Windows CI, RemoveUnusedVariableCodeFixProvider's edit also reformats the blank line right
    // before the statement it deletes, and that reformatting used Roslyn's FormattingOptions.NewLine
    // default — Environment.NewLine, i.e. "\r\n" on Windows — while _preamble/KindPrefix/TrailingPad
    // are all built with literal '\n' regardless of OS. The resulting edit's span, 1 byte longer per
    // newline than expected, straddled across preambleLength just enough to trip
    // RoslynCodeFixEngine.ApplyRelocatingPreambleEdits' preamble-safety check and discard the whole
    // fix — logged CI evidence: "TextChange(s): [160..175) -> \"\r\n\r\n\"" then "Discarding fix:
    // preamble-touching change [160..175) ... preambleLength=162". CSharpIntelliSenseService now
    // forces FormattingOptions.NewLine to "\n" explicitly so Roslyn's own formatting always matches
    // what the synthetic document actually contains, independent of OS.
    //
    // Can't force Environment.NewLine to "\r\n" on this (non-Windows) machine to reproduce the actual
    // failure, so this checks the one thing verifiable here: the workspace's option is genuinely "\n"
    // rather than silently falling back to Environment.NewLine (which happens to already be "\n" on
    // Linux/macOS — a doctrinal revert here wouldn't fail on THIS machine either, which is exactly
    // why the bug was invisible locally in the first place).
    [Fact]
    public void Constructor_Should_ForceFormattingNewLineOption_ToLineFeed()
    {
        using var service = CreateService();

        Assert.Equal("\n", service.FormattingNewLineOptionForTests);
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

    // SyncDocument's check-then-act on _lastCode/the AdhocWorkspace's
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

    // MetadataReference.CreateFromFile (PE-header + XML-doc parsing) used to
    // run again for every one of the ~167 default BCL reference assemblies on every new tab, even
    // though references are immutable and safe to share across unrelated workspaces. ReferenceCache
    // is internal (InternalsVisibleTo, same pattern as CSharpReferenceService.IsManagedAssembly)
    // so this can verify the cache directly rather than only inferring it indirectly.
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
