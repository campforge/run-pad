using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// SyncDocument built its live-analysis document as just "preamble + code",
// with no awareness of MdRunKind at all — so for MdRunKind.Program, where the *real* compiled program
// gets an implicit `Main();` call prepended (CSharpLanguageHost.ApplyKindTransform), the analysis
// model never saw that call. From Roslyn's point of view the user's own `void Main() { ... }` was a
// top-level local function nobody ever invoked — a textbook, correctly-reported CS8321 ("declared but
// never used") that nonetheless made every Program-kind script's entry point look broken in the
// editor. UpdateKind(MdRunKind.Program) makes SyncDocument mirror that same prefix.
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
    public async Task ImportedNamespaces_Should_NotConflictWithGlobalUsings()
    {
        using var service = CreateService();
        service.UpdateKind(MdRunKind.Statements);
        service.UpdateGlobalUsings(["System.Text"]);
        const string code = "global using Text = System.Text;\nusing System.Globalization;\nConsole.WriteLine(new Text.StringBuilder());";
        var diagnostics = await WaitForDiagnosticsAsync(service, code);
        Assert.DoesNotContain(diagnostics, d => d.Severity == Invoklet.Contracts.IntelliSense.DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task NotifyTextChanged_Should_NotFlagMainAsUnused_WhenKindIsProgram()
    {
        using var service = CreateService();
        service.UpdateKind(MdRunKind.Program);

        var diagnostics = await WaitForDiagnosticsAsync(service, ProgramKindScript);

        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("Main", StringComparison.Ordinal));
    }

    // Regression guard proving the assertion above actually exercises the fix rather than passing
    // vacuously: the exact same script, without ever calling UpdateKind (kind stays the default
    // MdRunKind.Statements) — this reproduces the pre-fix state, where SyncDocument had no way to know
    // about the implicit Main(); call at all, so Main() really is unreachable dead code as far as this
    // synthetic document is concerned.
    [Fact]
    public async Task NotifyTextChanged_Should_FlagMainAsUnused_WhenKindWasNeverUpdated()
    {
        using var service = CreateService();

        var diagnostics = await WaitForDiagnosticsAsync(service, ProgramKindScript);

        Assert.Contains(diagnostics, d => d.Message.Contains("Main", StringComparison.Ordinal));
    }

    // A static Main (also valid Program-kind, LINQPad-style, input — MdRunKindTransform calls it via
    // __InvokletProgram.Main() rather than new __InvokletProgram().Main()) is wrapped the same way, so the
    // synced document contains the same synthetic second entry point the real build does. Unlike the
    // real build, this workspace's compilation is always a library (CompilationOptions above,
    // OutputKind.DynamicallyLinkedLibrary) — entry-point resolution, and so CS7022, only exists for an
    // executable — so Roslyn's live analysis can never report it here regardless of CSharpScriptCompiler's
    // own structural filter; this guards that assumption rather than a separate fix.
    [Fact]
    public async Task NotifyTextChanged_Should_NotFlagCS7022_ForAStaticMain_WhenKindIsProgram()
    {
        using var service = CreateService();
        service.UpdateKind(MdRunKind.Program);
        const string code = "static void Main()\n{\n    Console.WriteLine(\"hi\");\n}\n";

        var diagnostics = await WaitForDiagnosticsAsync(service, code);

        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("entry point", StringComparison.OrdinalIgnoreCase));
    }

    // Position math sanity check: HeaderLength now includes the mirrored "Main();" prefix for
    // MdRunKind.Program, so a completion request at a position *inside* the user's own code must still
    // resolve against the right character offset in the live-analysis document, not one shifted by the
    // prefix's own length.
    [Fact]
    public async Task GetCompletionsAsync_Should_ResolveCorrectPosition_WhenKindIsProgram()
    {
        using var service = CreateService();
        service.UpdateKind(MdRunKind.Program);

        const string code = "void Main()\n{\n    Console.Wr\n}\n";
        var position = code.IndexOf("Console.Wr", StringComparison.Ordinal) + "Console.Wr".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "WriteLine");
    }

    // ApplyKindTransform now mirrors "await Main();" instead of
    // "Main();" when the user's Main is Task-returning — SyncDocument has to mirror that same,
    // longer, code-dependent prefix or this async-Main script would hit the exact same false-positive
    // CS8321 the two tests above were written to prevent for the plain void Main() case.
    private const string AsyncProgramKindScript =
        "async Task Main()\n{\n    await Task.Delay(1);\n    Console.WriteLine(\"hi\");\n}\n";

    [Fact]
    public async Task NotifyTextChanged_Should_NotFlagMainAsUnused_WhenMainIsAsyncTask()
    {
        using var service = CreateService();
        service.UpdateKind(MdRunKind.Program);

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
        service.UpdateKind(MdRunKind.Program);

        const string code = "async Task Main()\n{\n    Console.Wr\n}\n";
        var position = code.IndexOf("Console.Wr", StringComparison.Ordinal) + "Console.Wr".Length;

        var result = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result!.Items, i => i.DisplayText == "WriteLine");
    }

    // Reported by user 2026-07-16, live in the editor (not in an actual compile — Invoklet.Cli/a real
    // `dotnet build` were both already clean): opening an existing Program-kind file whose Main is
    // `async Task Main()` showed a false-positive "declared but never used" CS8321 in the diagnostics
    // panel that never went away on its own. Root cause was in EditorTabItemControl.SetDocumentContent,
    // not here — it used to set editor.Document (which synchronously fires TextChanged, which is what
    // actually calls NotifyTextChanged below) BEFORE calling RefreshIntelliSenseKind() (UpdateKind).
    // UpdateKind only invalidates the cached analysis document; it doesn't itself trigger a fresh
    // pass — so the very first analysis for a freshly opened file always ran under the stale default
    // (MdRunKind.Statements), and nothing re-ran it until the user's next edit. These two tests pin the
    // ordering that actually matters, one call to NotifyTextChanged each, no service-level fix needed.
    [Fact]
    public async Task NotifyTextChanged_Should_NotFlagAsyncMainAsUnused_OnItsVeryFirstPass_WhenKindIsAppliedFirst()
    {
        using var service = CreateService();

        // Mirrors EditorTabItemControl.SetDocumentContent's fixed ordering: RefreshIntelliSenseKind()
        // (UpdateKind) now runs before editor.Document is ever assigned.
        service.UpdateKind(MdRunKind.Program);
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
        service.UpdateKind(MdRunKind.Program); // too late for the pass that already ran

        Assert.Contains(diagnostics, d => d.Message.Contains("Main", StringComparison.Ordinal));
    }

    // The exact live-reported repro, in the EDITOR (not just at compile time — see
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
        service.UpdateKind(MdRunKind.Program);
        service.UpdateGlobalUsings(["System.Text.RegularExpressions"]);

        var diagnostics = await WaitForDiagnosticsAsync(service, RegexHelperProgramKindScript);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        // Every reported span (warnings/info, if any) must land within the user's own code — see
        // CSharpIntelliSenseService.ComputeDiagnosticsAndCodeFixesAsync's upper-bound clamp:
        // without it, a diagnostic anchored in the mirrored KindSuffix (the synthetic
        // trampoline/closing brace) could leak an out-of-range span to the editor.
        Assert.All(diagnostics, d =>
        {
            Assert.InRange(d.StartOffset, 0, RegexHelperProgramKindScript.Length);
            Assert.InRange(d.EndOffset, 0, RegexHelperProgramKindScript.Length);
        });
    }

    // A classic program (usings, a namespace, a class with a static Main) is analyzed as written,
    // like it is compiled: no wrapper errors, and a real error is underlined where the user typed it.
    [Fact]
    public async Task AClassicProgram_Should_BeAnalyzedAsWritten()
    {
        using var service = CreateService();
        service.UpdateKind(MdRunKind.Program);
        const string code = "using System;\nusing Out = System.Console;\n\nnamespace N\n{\n    class Program\n    {\n        static void Main()\n        {\n            int x = \"text\";\n            Out.WriteLine(x);\n        }\n    }\n}\n";

        var diagnostics = await WaitForDiagnosticsAsync(service, code);

        var error = Assert.Single(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(code.IndexOf("\"text\"", StringComparison.Ordinal), error.StartOffset);
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("__Invoklet", StringComparison.Ordinal)
            || d.Message.Contains("using clause", StringComparison.Ordinal));
    }

    // The editor uses the script's unsafe setting the way the build does: CS0227 only without it.
    [Fact]
    public async Task UnsafeCode_Should_BeFlagged_OnlyWithoutTheScriptsSetting()
    {
        const string code = "unsafe static void Main() { int x = 1; int* p = &x; Console.WriteLine(*p); }\n";
        using (var off = CreateService())
        {
            off.UpdateKind(MdRunKind.Program);
            Assert.Contains(await WaitForDiagnosticsAsync(off, code), d => d.Message.Contains("unsafe", StringComparison.OrdinalIgnoreCase)
                && d.Severity == DiagnosticSeverity.Error);
        }
        using var on = CreateService();
        on.UpdateKind(MdRunKind.Program);
        on.UpdateExecutionContext(new Invoklet.Contracts.Models.ExecutionRequest { AllowUnsafe = true });
        Assert.DoesNotContain(await WaitForDiagnosticsAsync(on, code), d => d.Severity == DiagnosticSeverity.Error);
    }

    // The editor shows nothing wrong with an empty Statements script, as the build now runs it.
    [Fact]
    public async Task AnEmptyStatementsScript_Should_HaveNoErrorsInTheEditor()
    {
        using var service = CreateService();
        service.UpdateKind(MdRunKind.Statements);

        var diagnostics = await WaitForDiagnosticsAsync(service, "// only comments\nusing System.Text;\n");

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    // The editor classifies a Program with the same symbols as the build: a Main under DEBUG is the
    // entry point, not an unused local function.
    [Fact]
    public async Task AMemberMainUnderDebug_Should_NotBeFlaggedAsUnused()
    {
        using var service = CreateService();
        service.UpdateKind(MdRunKind.Program);

        var diagnostics = await WaitForDiagnosticsAsync(service, "#if DEBUG\nvoid Main()\n{\n    Console.WriteLine(\"hi\");\n}\n#endif\n");

        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("Main", StringComparison.Ordinal));
    }
    [Fact]
    public async Task ExtensionClass_Should_ResolveCallsAndMapDefinitionToOriginalSource()
    {
        using var service = CreateService();
        service.UpdateKind(MdRunKind.Program);
        const string code = "void Main() { Console.WriteLine(2.Twice()); }\nstatic class Extensions { public static int Twice(this int n) => n * 2; }\nvoid Helper() { Console.WriteLine(3.Twice()); }\n";
        var diagnostics = await WaitForDiagnosticsAsync(service, code);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var definition = await service.GetDefinitionAsync(code, code.IndexOf("Twice", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        Assert.NotNull(definition);
        Assert.True(definition.IsInSource);
        Assert.Equal(code.IndexOf("Twice(this", StringComparison.Ordinal), definition.Offset);
        var lastDefinition = await service.GetDefinitionAsync(code, code.LastIndexOf("Twice", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        Assert.NotNull(lastDefinition);
        Assert.Equal(definition.Offset, lastDefinition.Offset);
    }

    [Fact]
    public async Task ExtensionProjection_Should_MapDiagnosticsAndRenameAcrossClassBoundaries()
    {
        using var service = CreateService();
        service.UpdateKind(MdRunKind.Program);
        const string code = "void Main() { Console.WriteLine(2.Twice()); }\nstatic class Extensions { public static int Twice(this int n) => n * 2; }\nvoid Helper() { MissingType x; Console.WriteLine(3.Twice()); }\n";
        var diagnostics = await WaitForDiagnosticsAsync(service, code);
        var error = Assert.Single(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(code.IndexOf("MissingType", StringComparison.Ordinal), error.StartOffset);
        var rename = await service.GetRenameInfoAsync(code, code.IndexOf("Twice", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        Assert.NotNull(rename);
        Assert.Equal(3, rename.Spans.Count);
        Assert.All(rename.Spans, span => Assert.Equal("Twice", code.Substring(span.Start, span.Length)));
    }

    [Fact]
    public async Task ExtensionProjection_Should_MaterializeRefactoringWithoutSyntheticBraces()
    {
        using var service = CreateService();
        service.UpdateKind(MdRunKind.Program);
        const string code = "void Main() { Console.WriteLine(2.Twice()); }\nstatic class Extensions\n{\n    public static int Twice(this int n)\n    {\n        return n * 2;\n    }\n}\n";
        var fixes = await service.GetRefactoringsAsync(code, code.IndexOf("return n", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        Assert.NotNull(fixes);
        var fix = Assert.Single(fixes, f => f.Title.Contains("expression body", StringComparison.OrdinalIgnoreCase));
        var result = await service.MaterializeFixAsync(code, fix.Key, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Contains("=> n * 2", result.NewCode);
        Assert.DoesNotContain("__Invoklet", result.NewCode);
        Assert.DoesNotContain("partial class", result.NewCode);
        Assert.DoesNotContain(await WaitForDiagnosticsAsync(service, result.NewCode), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ExtensionProjection_Should_CompleteAndClassifySourceAfterLiftedTypes()
    {
        using var service = CreateService();
        service.UpdateKind(MdRunKind.Program);
        const string code = "void Main() { Console.WriteLine(2.Twice()); }\nstatic class Extensions { public static int Twice(this int n) => n * 2; }\nvoid Helper() { Console.WriteLine(3.Twice()); }\n";
        var position = code.IndexOf("3.Twice", StringComparison.Ordinal) + 2;
        var completions = await service.GetCompletionsAsync(code, position, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(completions);
        Assert.Contains(completions.Items, item => item.DisplayText == "Twice");
        var symbols = await service.GetDocumentHighlightsAsync(code, position, TestContext.Current.CancellationToken);
        Assert.NotNull(symbols);
        Assert.Equal(3, symbols.Count);
        Assert.All(symbols, span => Assert.Equal("Twice", code.Substring(span.Start, span.Length)));
    }

}
