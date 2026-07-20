using DotPad.Contracts.IntelliSense;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #74 "Пайплайн анализаторов" (сто третий проход) — RoslynAnalyzerEngine, wired into
// NotifyTextChanged's existing background pass alongside the compiler's own diagnostics (see
// CSharpIntelliSenseBackgroundAnalysisTests.cs for that shared pass's other coverage). Real
// Microsoft.CodeAnalysis.CSharp.Features IDE0xxx analyzers, not mocked — same rationale as every
// other "real Roslyn, not a fake" test this session: the thing under test is whether DotPad's
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
}
