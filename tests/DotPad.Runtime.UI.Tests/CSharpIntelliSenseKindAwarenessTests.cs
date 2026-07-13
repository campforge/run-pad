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
}
