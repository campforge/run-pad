using DotPad.Contracts.IntelliSense;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// ANALYSIS.md TODO #66: SyncDocument built its live-analysis document as just "preamble + code",
// with no awareness of RpadKind at all — so for RpadKind.Program, where the *real* compiled program
// gets an implicit `Main();` call prepended (CSharpLanguageHost.ApplyKindTransform), the analysis
// model never saw that call. From Roslyn's point of view the user's own `void Main() { ... }` was a
// top-level local function nobody ever invoked — a textbook, correctly-reported CS8321 ("declared but
// never used") that nonetheless made every Program-kind script's entry point look broken in the
// editor. UpdateKind(RpadKind.Program) makes SyncDocument mirror that same prefix.
public class CSharpIntelliSenseKindAwarenessTests
{
    private const string ProgramKindScript = "void Main()\n{\n    Console.WriteLine(\"hi\");\n}\n";

    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
    }

    private static async Task<IReadOnlyList<IDiagnosticInfo>> WaitForDiagnosticsAsync(
        CSharpIntelliSenseService service, string code)
    {
        IReadOnlyList<IDiagnosticInfo>? diagnostics = null;
        service.DiagnosticsChanged += (_, d) => diagnostics = d;
        service.NotifyTextChanged(code);

        for (var i = 0; i < 150 && diagnostics is null; i++)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.NotNull(diagnostics);
        return diagnostics!;
    }

    [Fact]
    public async Task NotifyTextChanged_Should_NotFlagMainAsUnused_WhenKindIsProgram()
    {
        using var service = CreateService();
        service.UpdateKind(RpadKind.Program);

        var diagnostics = await WaitForDiagnosticsAsync(service, ProgramKindScript);

        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("Main", StringComparison.Ordinal));
    }

    // Regression guard proving the assertion above actually exercises the fix rather than passing
    // vacuously: the exact same script, without ever calling UpdateKind (kind stays the default
    // RpadKind.Statements) — this reproduces the pre-fix state, where SyncDocument had no way to know
    // about the implicit Main(); call at all, so Main() really is unreachable dead code as far as this
    // synthetic document is concerned.
    [Fact]
    public async Task NotifyTextChanged_Should_FlagMainAsUnused_WhenKindWasNeverUpdated()
    {
        using var service = CreateService();

        var diagnostics = await WaitForDiagnosticsAsync(service, ProgramKindScript);

        Assert.Contains(diagnostics, d => d.Message.Contains("Main", StringComparison.Ordinal));
    }

    // Position math sanity check: HeaderLength now includes the mirrored "Main();" prefix for
    // RpadKind.Program, so a completion request at a position *inside* the user's own code must still
    // resolve against the right character offset in the live-analysis document, not one shifted by the
    // prefix's own length.
    [Fact]
    public async Task GetCompletionsAsync_Should_ResolveCorrectPosition_WhenKindIsProgram()
    {
        using var service = CreateService();
        service.UpdateKind(RpadKind.Program);

        const string code = "void Main()\n{\n    Console.Wr\n}\n";
        var position = code.IndexOf("Console.Wr", StringComparison.Ordinal) + "Console.Wr".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "WriteLine");
    }

    // TODO.md #63 (async Task Main): ApplyKindTransform now mirrors "await Main();" instead of
    // "Main();" when the user's Main is Task-returning — SyncDocument has to mirror that same,
    // longer, code-dependent prefix or this async-Main script would hit the exact same false-positive
    // CS8321 the two tests above were written to prevent for the plain void Main() case.
    private const string AsyncProgramKindScript =
        "async Task Main()\n{\n    await Task.Delay(1);\n    Console.WriteLine(\"hi\");\n}\n";

    [Fact]
    public async Task NotifyTextChanged_Should_NotFlagMainAsUnused_WhenMainIsAsyncTask()
    {
        using var service = CreateService();
        service.UpdateKind(RpadKind.Program);

        var diagnostics = await WaitForDiagnosticsAsync(service, AsyncProgramKindScript);

        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("Main", StringComparison.Ordinal));
    }

    // Same position-math sanity check as GetCompletionsAsync_Should_ResolveCorrectPosition_WhenKindIsProgram
    // above, but against the longer "await Main();" prefix — HeaderLength must reflect whichever
    // prefix SyncDocument actually mirrored for this exact code, not a fixed length.
    [Fact]
    public async Task GetCompletionsAsync_Should_ResolveCorrectPosition_WhenMainIsAsyncTask()
    {
        using var service = CreateService();
        service.UpdateKind(RpadKind.Program);

        const string code = "async Task Main()\n{\n    Console.Wr\n}\n";
        var position = code.IndexOf("Console.Wr", StringComparison.Ordinal) + "Console.Wr".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "WriteLine");
    }

    // Reported by user 2026-07-16, live in the editor (not in an actual compile — DotPad.Cli/a real
    // `dotnet build` were both already clean): opening an existing Program-kind file whose Main is
    // `async Task Main()` showed a false-positive "declared but never used" CS8321 in the diagnostics
    // panel that never went away on its own. Root cause was in EditorTabItemControl.SetDocumentContent,
    // not here — it used to set editor.Document (which synchronously fires TextChanged, which is what
    // actually calls NotifyTextChanged below) BEFORE calling RefreshIntelliSenseKind() (UpdateKind).
    // UpdateKind only invalidates the cached analysis document; it doesn't itself trigger a fresh
    // pass — so the very first analysis for a freshly opened file always ran under the stale default
    // (RpadKind.Statements), and nothing re-ran it until the user's next edit. These two tests pin the
    // ordering that actually matters, one call to NotifyTextChanged each, no service-level fix needed.
    [Fact]
    public async Task NotifyTextChanged_Should_NotFlagAsyncMainAsUnused_OnItsVeryFirstPass_WhenKindIsAppliedFirst()
    {
        using var service = CreateService();

        // Mirrors EditorTabItemControl.SetDocumentContent's fixed ordering: RefreshIntelliSenseKind()
        // (UpdateKind) now runs before editor.Document is ever assigned.
        service.UpdateKind(RpadKind.Program);
        var diagnostics = await WaitForDiagnosticsAsync(service, AsyncProgramKindScript);

        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("Main", StringComparison.Ordinal));
    }

    // Regression guard proving the ordering above is what actually matters, not just calling
    // UpdateKind at some point: the exact same script and the exact same eventual UpdateKind call,
    // just in SetDocumentContent's old (buggy) order — kind applied only after the first pass already
    // ran and reported the false positive. If this ever starts passing, the bug is back.
    [Fact]
    public async Task NotifyTextChanged_Should_LeaveAStaleAsyncMainWarning_WhenKindIsAppliedAfterTheFirstPass()
    {
        using var service = CreateService();

        var diagnostics = await WaitForDiagnosticsAsync(service, AsyncProgramKindScript); // kind still Statements
        service.UpdateKind(RpadKind.Program); // too late for the pass that already ran

        Assert.Contains(diagnostics, d => d.Message.Contains("Main", StringComparison.Ordinal));
    }

    // TODO.md #107: the exact live-reported repro, in the EDITOR (not just at compile time — see
    // CSharpLanguageHostProgramKindCompileTests for the real dotnet-build side of this same fix). Under
    // the old raw-top-level-statements mirroring, this script's live-analysis document would have shown
    // real CS0165/CS0106-equivalent errors as squiggles, not just at Run time. KindPrefix/KindSuffix now
    // mirror the class-wrapping transform, so the field and the modified method live inside the same
    // synthetic class the real compiler wraps them in — no error-severity diagnostics at all expected.
    private const string RegexHelperProgramKindScript =
        "Regex regexp = new Regex(@\"_(?<resolution>(\\d+)x(\\d+))\\.\");\n\n"
        + "async Task Main(){\n\tvar filename = \"100X100_123\";\n\tGetResolution(filename).Dump();\n}\n\n"
        + "private string? GetResolution(string fileName)\n{\n"
        + "    return regexp.Matches(fileName).Cast<Match>().ToList().FirstOrDefault()?.Groups[\"resolution\"].Value;\n}\n";

    [Fact]
    public async Task NotifyTextChanged_Should_ReportNoErrors_ForALinqPadStyleScript_WithAFieldBelowMain_AndAModifiedHelperMethod()
    {
        using var service = CreateService();
        service.UpdateKind(RpadKind.Program);
        service.UpdateGlobalUsings(["System.Text.RegularExpressions"]);

        var diagnostics = await WaitForDiagnosticsAsync(service, RegexHelperProgramKindScript);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        // Every reported span (warnings/info, if any) must land within the user's own code — see
        // CSharpIntelliSenseService.ComputeDiagnosticsAndCodeFixesAsync's upper-bound clamp (TODO.md
        // #107): without it, a diagnostic anchored in the mirrored KindSuffix (the synthetic
        // trampoline/closing brace) could leak an out-of-range span to the editor.
        Assert.All(diagnostics, d =>
        {
            Assert.InRange(d.StartOffset, 0, RegexHelperProgramKindScript.Length);
            Assert.InRange(d.EndOffset, 0, RegexHelperProgramKindScript.Length);
        });
    }
}
