using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Contracts.IntelliSense;
using RunPad.Contracts.Models;
using RunPad.Debug;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.Compiler;
using RunPad.Languages.CSharp.IntelliSense;
using RunPad.Languages.CSharp.Sdk;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Compiler;
using Xunit;
using DiagnosticSeverity = RunPad.Contracts.IntelliSense.DiagnosticSeverity;

namespace RunPad.Runtime.UI.Tests;

// The build runs the source generators of the target's targeting pack ([GeneratedRegex],
// System.Text.Json contexts, [LibraryImport]); the editor has to see what they generate too, or it
// reports errors for code that compiles and offers no completion for generated members. Only the
// installed pack of the framework the tests run on is used.
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
public sealed class SourceGeneratorIntelliSenseTests : IAsyncDisposable
{
    private const string Regex = """
        Console.WriteLine(Patterns.Digits().IsMatch("123"));

        static partial class Patterns
        {
            [System.Text.RegularExpressions.GeneratedRegex(@"\d+")]
            public static partial System.Text.RegularExpressions.Regex Digits();
        }
        """;

    private const string Json = """
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(42, NumberContext.Default.Int32));

        [System.Text.Json.Serialization.JsonSerializable(typeof(int))]
        partial class NumberContext : System.Text.Json.Serialization.JsonSerializerContext
        {
        }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("runpad-generator-intellisense").FullName;
    private readonly string _target = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
    private int _dirs;

    public ValueTask DisposeAsync() => TempDirectoryCleanup.DeleteAsync(_root);

    private async Task<List<string>> BuildErrorsAsync(string code)
    {
        var output = Directory.CreateDirectory(Path.Combine(_root, $"out{++_dirs}")).FullName;
        var options = new CompiledDotnetLanguageHostOptions
        {
            Platform = new DotnetSdk(_target, _target, _target[3..] + ".0", _target),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = output,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            OutputDirectory = output,
            RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
            Kind = RpadKind.Statements,
        };
        using var compiler = new CSharpScriptCompiler(options, NullLogger.Instance);
        var result = await compiler.Compilation(code, TestContext.Current.CancellationToken);
        return result.Errors.Select(e => $"{e.Code} {e.Message}").ToList();
    }

    // Every error and warning of a real build, with its position in the script.
    private async Task<List<(string Code, uint Line, uint Column, string Message)>> BuildDiagnosticsAsync(string code)
    {
        var output = Directory.CreateDirectory(Path.Combine(_root, $"out{++_dirs}")).FullName;
        var options = new CompiledDotnetLanguageHostOptions
        {
            Platform = new DotnetSdk(_target, _target, _target[3..] + ".0", _target),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = output,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            OutputDirectory = output,
            RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
            Kind = RpadKind.Statements,
        };
        using var compiler = new CSharpScriptCompiler(options, NullLogger.Instance);
        var result = await compiler.Compilation(code, TestContext.Current.CancellationToken);
        return result.Errors.Concat(result.Warnings)
            .Select(e => (e.Code, e.LineSymbol.Line, e.LineSymbol.Symbol, e.Message)).ToList();
    }

    private CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var service = new CSharpIntelliSenseService(
            referenceService.GetDefault().Append(referenceService.GetExtensionReference()), NullLogger<CSharpIntelliSenseService>.Instance)
        {
            BackgroundAnalysisDebounce = TimeSpan.Zero,
        };
        service.UpdatePackageReferences(new Dictionary<string, Package>(), new PackageReferenceContext(_target, null, null, "dotnet"));
        return service;
    }

    private static async Task<IReadOnlyList<IDiagnosticInfo>> EditorDiagnosticsAsync(CSharpIntelliSenseService service, string code)
    {
        var result = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDiagnostics(string analyzed, IReadOnlyList<IDiagnosticInfo> diagnostics)
        {
            if (analyzed == code) result.TrySetResult(diagnostics);
        }
        service.DiagnosticsChanged += OnDiagnostics;
        try
        {
            service.NotifyTextChanged(code);
            return await result.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        }
        finally
        {
            service.DiagnosticsChanged -= OnDiagnostics;
        }
    }

    private static async Task<List<string>> EditorErrorsAsync(CSharpIntelliSenseService service, string code)
    {
        var result = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDiagnostics(string analyzed, IReadOnlyList<IDiagnosticInfo> diagnostics)
        {
            if (analyzed == code) result.TrySetResult(diagnostics);
        }
        service.DiagnosticsChanged += OnDiagnostics;
        try
        {
            service.NotifyTextChanged(code);
            var diagnostics = await result.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            return diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message).ToList();
        }
        finally
        {
            service.DiagnosticsChanged -= OnDiagnostics;
        }
    }

    private static string RegexWithPattern(string pattern) => $$"""
        Console.WriteLine(Patterns.Digits().IsMatch("123"));

        static partial class Patterns
        {
            [System.Text.RegularExpressions.GeneratedRegex(@"{{pattern}}")]
            public static partial System.Text.RegularExpressions.Regex Digits();
        }
        """;

    // What the generators themselves report: an invalid [GeneratedRegex] pattern is SYSLIB1042 at the
    // attribute (line 5, column 5) in the build, and the editor marks the same place — on the user's
    // own text — and drops it again once the pattern is fixed.
    [Fact]
    public async Task AnInvalidGeneratedRegex_Should_ReportTheGeneratorsError_AtTheAttribute_InBuildAndEditor()
    {
        var valid = RegexWithPattern(@"\d+");
        var invalid = RegexWithPattern("(");
        const string invalidPattern = "The specified regex is invalid.";

        Assert.Contains(await BuildDiagnosticsAsync(invalid),
            d => d.Code == "SYSLIB1042" && d.Line == 5 && d.Column == 5 && d.Message.Contains(invalidPattern, StringComparison.Ordinal));

        using var service = CreateService();
        Assert.Empty(Errors(await EditorDiagnosticsAsync(service, valid)));

        var error = Assert.Single(await EditorDiagnosticsAsync(service, invalid),
            d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains(invalidPattern, StringComparison.Ordinal));
        var attribute = invalid.IndexOf("[System.Text.RegularExpressions.GeneratedRegex", StringComparison.Ordinal);
        Assert.Equal(attribute, error.StartOffset);
        Assert.InRange(error.EndOffset, attribute + 1, invalid.Length);

        Assert.Empty(Errors(await EditorDiagnosticsAsync(service, valid)));
    }

    // A System.Text.Json generator warning (SYSLIB1038, a [JsonInclude] member it can't access) is on
    // the member's name in both, without making the build fail.
    [Fact]
    public async Task AJsonGeneratorWarning_Should_MarkTheMemberName_InBuildAndEditor()
    {
        const string code = """
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new Item(), ItemContext.Default.Item));

            public class Item
            {
                [System.Text.Json.Serialization.JsonInclude]
                private int Hidden { get; set; }
                public int Shown { get; set; }
            }

            [System.Text.Json.Serialization.JsonSerializable(typeof(Item))]
            partial class ItemContext : System.Text.Json.Serialization.JsonSerializerContext
            {
            }
            """;
        const string notVisible = "has been annotated with the JsonIncludeAttribute but is not visible to the source generator";

        Assert.Empty(await BuildErrorsAsync(code));
        Assert.Contains(await BuildDiagnosticsAsync(code), d => d.Code == "SYSLIB1038" && d.Line == 6 && d.Column == 17);

        using var service = CreateService();
        var diagnostics = await EditorDiagnosticsAsync(service, code);
        Assert.Empty(Errors(diagnostics));
        var warning = Assert.Single(diagnostics, d => d.Message.Contains(notVisible, StringComparison.Ordinal));
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("Hidden", code[warning.StartOffset..warning.EndOffset]);
    }

    private static List<IDiagnosticInfo> Errors(IReadOnlyList<IDiagnosticInfo> diagnostics) =>
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    [Fact]
    public async Task AGeneratedRegex_Should_HaveNoEditorErrors_WhenItCompiles()
    {
        Assert.Empty(await BuildErrorsAsync(Regex));

        using var service = CreateService();
        Assert.Empty(await EditorErrorsAsync(service, Regex));
    }

    // Generated code follows the edits: a renamed partial method gets its implementation, and nothing
    // is left of the old one.
    [Fact]
    public async Task GeneratedCode_Should_FollowEdits()
    {
        using var service = CreateService();
        Assert.Empty(await EditorErrorsAsync(service, Regex));

        var renamed = Regex.Replace("Digits", "Numbers");
        Assert.Empty(await EditorErrorsAsync(service, renamed));
        Assert.Contains(await EditorErrorsAsync(service, renamed.Replace("Patterns.Numbers()", "Patterns.Digits()")),
            m => m.Contains("'Patterns' does not contain a definition for 'Digits'", StringComparison.Ordinal));
    }

    // The generators belong to the target: a target without a targeting pack has none (and the
    // build would have none either), and they come back with a target that has one.
    [Fact]
    public async Task ATargetWithoutAPack_Should_DropTheGenerators_AndTheNextTargetBringsThemBack()
    {
        using var service = CreateService();
        Assert.NotEmpty(service.GeneratorPathsForTests);
        Assert.Empty(await EditorErrorsAsync(service, Regex));

        service.UpdatePackageReferences(new Dictionary<string, Package>(), new PackageReferenceContext("net10.9", null, null, "dotnet"));
        Assert.Empty(service.GeneratorPathsForTests);
        Assert.Contains(await EditorErrorsAsync(service, Regex),
            m => m.Contains("must have an implementation part", StringComparison.Ordinal));

        service.UpdatePackageReferences(new Dictionary<string, Package>(), new PackageReferenceContext(_target, null, null, "dotnet"));
        Assert.NotEmpty(service.GeneratorPathsForTests);
        Assert.Empty(await EditorErrorsAsync(service, Regex));
    }

    [Fact]
    public async Task AJsonSerializerContext_Should_ShowItsGeneratedMembers_InTheEditor()
    {
        Assert.Empty(await BuildErrorsAsync(Json));

        using var service = CreateService();
        Assert.Empty(await EditorErrorsAsync(service, Json));

        // Completion for a generated member, inside a complete statement.
        const string prefix = "var info = NumberContext.Default.";
        var code = prefix + "Int32;\n" + Json[Json.IndexOf('[')..];
        var completions = await service.GetCompletionsAsync(code, prefix.Length, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(completions);
        Assert.Contains(completions.Items, i => i.DisplayText == "Int32");
    }
}
