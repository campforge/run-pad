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


        var codeFixes = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Contains(codeFixes, f => f.UsingsToAdd.Contains("System.Text.RegularExpressions"));


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
