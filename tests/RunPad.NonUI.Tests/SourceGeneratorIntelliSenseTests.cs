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
