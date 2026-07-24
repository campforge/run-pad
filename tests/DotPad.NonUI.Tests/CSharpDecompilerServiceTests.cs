using DotPad.Build.Core.Models;
using DotPad.Contracts.IntelliSense;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.Sdk;
using DotPad.Languages.DotnetCommon;
using DotPad.Languages.DotnetCommon.Compiler;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #62 "Go to Definition" (F12, user request 2026-07-19) — external half. Real dotnet-build
// integration test (no mocking of the compiler): the thing under test is whether
// ICSharpCode.Decompiler can locate a SPECIFIC overload's declaration line inside a type it just
// decompiled from a binary MsBuildProcessCompiler produced, which pure-logic unit testing can't stand
// in for.
//
// Proход 134 (CI-cost pass) — 7 tests used to each spawn their own real `dotnet build` for one of
// only 3 distinct source snippets (~1.5s per build, ~10.5s total). Decompile/ResolveReferenceAtPosition
// only READ the resulting binary/syntax tree — nothing here mutates shared state across tests — so an
// IClassFixture compiles each snippet exactly once per test class run and every test just reads the
// shared result, cutting it to 3 real builds (~4.5s) with zero coverage loss and each test still
// fully isolated in its own [Fact].
// Category=RealBuild lets CI's fast Windows/macOS PR lane exclude this whole group via
// `dotnet test --filter "Category!=RealBuild"` — see .github/workflows/dotnet-desktop.yml.
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
public class CSharpDecompilerServiceTests : IClassFixture<CSharpDecompilerServiceTests.CompiledFixtures>
{
    private readonly CompiledFixtures _fixtures;

    public CSharpDecompilerServiceTests(CompiledFixtures fixtures)
    {
        _fixtures = fixtures;
    }

    private sealed record FakeDefinitionResult(
        bool IsInSource, int Offset, string? AssemblyPath, string? ContainingTypeFullName,
        string? MemberName, IReadOnlyList<string> ParameterTypeNames, string DisplayName) : IDefinitionResult;

    private const string Code = """
        IlDisassemblerProbe.Pick(1);
        IlDisassemblerProbe.Pick("x");
        class IlDisassemblerProbe
        {
            public static void Pick(int number) { System.Console.WriteLine(number); }
            public static void Pick(string text) { System.Console.WriteLine(text); }
        }
        """;

    // TODO.md #62 "рекурсивный F12 внутри декомпилированного окна" (user request 2026-07-19) —
    // ResolveReferenceAtPosition, the other half that makes navigation recursive: given a caret
    // position inside a SyntaxTree Decompile already produced, resolve what's referenced there into
    // another IDefinitionResult that Decompile itself can consume — same round trip dnSpy/ILSpy's own
    // in-tab F12 does, minus any dependency on Roslyn (the tree isn't Roslyn-backed at all).
    private const string RecursiveCode = """
        Caller.DoWork();
        class Caller
        {
            public static void DoWork() { Callee.Helper(); }
        }
        class Callee
        {
            public static void Helper() { System.Console.WriteLine("helper"); }
        }
        """;

    // TODO.md #62 "ILSpy-обозреватель сборок" (user request 2026-07-19) — EnumerateTypes/DecompileType,
    // the third leg alongside Decompile/ResolveReferenceAtPosition above: browsing an arbitrary DLL,
    // not one reached by following a reference. The lambda closing over `x` forces the compiler to
    // generate a display-class (nested inside Alpha, name starting with '<') — real noise to filter,
    // not a hypothetical one.
    private const string ExplorerCode = """
        _ = 0;

        namespace Foo.Bar
        {
            public class Alpha
            {
                public static System.Func<int, int> MakeAdder(int x) => y => x + y;
            }
        }
        namespace Foo.Baz
        {
            public class Beta { }
        }
        """;

    public sealed class CompiledFixtures : IAsyncLifetime
    {
        public string CodeBinary { get; private set; } = null!;
        public string RecursiveCodeBinary { get; private set; } = null!;
        public string ExplorerCodeBinary { get; private set; } = null!;

        private readonly List<string> _outputDirs = [];

        public async ValueTask InitializeAsync()
        {
            CodeBinary = await CompileAsync(Code);
            RecursiveCodeBinary = await CompileAsync(RecursiveCode);
            ExplorerCodeBinary = await CompileAsync(ExplorerCode);
        }

        private async Task<string> CompileAsync(string code)
        {
            var outputDir = Directory.CreateTempSubdirectory("dotpad-decompiler-test").FullName;
            _outputDirs.Add(outputDir);

            var sdk = new DotnetSdkProvider().Get().First();
            var runtimeLib = new CSharpReferenceService().GetExtensionReference();
            using var compiler = new MsBuildProcessCompiler(
                CSharpProjectProfile.Instance,
                sdk: sdk,
                executionOptions: new ExecutionOptions
                {
                    WorkingDirectory = outputDir,
                    Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                    Debugger = new DebuggerOptions { DebuggerPath = "" },
                },
                outputDir: outputDir,
                dotPadRuntimeLib: runtimeLib,
                outputName: "Query",
                nugetPackages: new Dictionary<string, string>(),
                usings: [],
                assemblyReferences: [],
                projectReferences: [],
                logger: NullLogger<MsBuildProcessCompiler>.Instance);

            var result = await compiler.Compilation(code, CancellationToken.None);
            if (!result.Success)
                throw new InvalidOperationException(
                    "Shared fixture compilation failed: " + string.Join('\n', result.Errors.Select(e => e.Message)));
            return result.OutputBinaryFile!;
        }

        public ValueTask DisposeAsync()
        {
            foreach (var dir in _outputDirs)
                Directory.Delete(dir, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void Decompile_Should_LocateTheExactOverload_DisambiguatedByParameterTypeNames()
    {
        var definition = new FakeDefinitionResult(
            IsInSource: false, Offset: 0, AssemblyPath: _fixtures.CodeBinary,
            ContainingTypeFullName: "IlDisassemblerProbe", MemberName: "Pick",
            ParameterTypeNames: ["String"], DisplayName: "IlDisassemblerProbe.Pick(string)");

        var decompiled = CSharpDecompilerService.Decompile(definition);

        var lines = decompiled.Text.Split('\n');
        var line = lines[decompiled.Line - 1];
        Assert.Contains("Pick", line);
        Assert.Contains("string", line);
        Assert.DoesNotContain("int number", line);
    }

    [Fact]
    public void Decompile_Should_LocateTheType_WhenMemberNameIsNull()
    {
        var definition = new FakeDefinitionResult(
            IsInSource: false, Offset: 0, AssemblyPath: _fixtures.CodeBinary,
            ContainingTypeFullName: "IlDisassemblerProbe", MemberName: null,
            ParameterTypeNames: [], DisplayName: "IlDisassemblerProbe");

        var decompiled = CSharpDecompilerService.Decompile(definition);

        var lines = decompiled.Text.Split('\n');
        Assert.Contains("class IlDisassemblerProbe", string.Join('\n', lines.Skip(decompiled.Line - 1).Take(3)));
    }

    [Fact]
    public void ResolveReferenceAtPosition_Should_ResolveACallSite_ToItsOwnDeclaration()
    {
        var callerDefinition = new FakeDefinitionResult(
            IsInSource: false, Offset: 0, AssemblyPath: _fixtures.RecursiveCodeBinary,
            ContainingTypeFullName: "Caller", MemberName: null,
            ParameterTypeNames: [], DisplayName: "Caller");
        var caller = CSharpDecompilerService.Decompile(callerDefinition);
        Assert.NotNull(caller.SyntaxTree);

        var (line, column) = FindPosition(caller.Text, "Helper");

        var reference = CSharpDecompilerService.ResolveReferenceAtPosition(caller.SyntaxTree!, line, column);

        Assert.NotNull(reference);
        Assert.Equal("Callee", reference!.ContainingTypeFullName);
        Assert.Equal("Helper", reference.MemberName);
        Assert.Equal(_fixtures.RecursiveCodeBinary, reference.AssemblyPath);
    }

    [Fact]
    public void ResolveReferenceAtPosition_Result_Should_BeDirectlyConsumableByDecompile()
    {
        var callerDefinition = new FakeDefinitionResult(
            IsInSource: false, Offset: 0, AssemblyPath: _fixtures.RecursiveCodeBinary,
            ContainingTypeFullName: "Caller", MemberName: null,
            ParameterTypeNames: [], DisplayName: "Caller");
        var caller = CSharpDecompilerService.Decompile(callerDefinition);
        var (line, column) = FindPosition(caller.Text, "Helper");
        var reference = CSharpDecompilerService.ResolveReferenceAtPosition(caller.SyntaxTree!, line, column);
        Assert.NotNull(reference);

        var callee = CSharpDecompilerService.Decompile(reference!);

        var lines = callee.Text.Split('\n');
        Assert.Contains("Helper", lines[callee.Line - 1]);
    }

    [Fact]
    public void ResolveReferenceAtPosition_Should_ReturnNull_WhenNothingIsResolvableThere()
    {
        var callerDefinition = new FakeDefinitionResult(
            IsInSource: false, Offset: 0, AssemblyPath: _fixtures.RecursiveCodeBinary,
            ContainingTypeFullName: "Caller", MemberName: null,
            ParameterTypeNames: [], DisplayName: "Caller");
        var caller = CSharpDecompilerService.Decompile(callerDefinition);

        // Far past the end of the actual text — no AST node contains this position at all.
        var reference = CSharpDecompilerService.ResolveReferenceAtPosition(caller.SyntaxTree!, 9999, 1);

        Assert.Null(reference);
    }

    // 1-based (line, column) of the first character of the first occurrence of `needle`, matching
    // the (line, column) coordinate space AstNode.Contains/TextLocation use.
    private static (int Line, int Column) FindPosition(string text, string needle)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var col = lines[i].IndexOf(needle, StringComparison.Ordinal);
            if (col >= 0) return (i + 1, col + 1);
        }
        throw new InvalidOperationException($"'{needle}' not found in decompiled text.");
    }

    [Fact]
    public void EnumerateTypes_Should_ListTopLevelTypes_AcrossNamespaces_ExcludingCompilerGeneratedNoise()
    {
        var types = CSharpDecompilerService.EnumerateTypes(_fixtures.ExplorerCodeBinary);

        Assert.Contains(types, t => t is { Namespace: "Foo.Bar", Name: "Alpha" });
        Assert.Contains(types, t => t is { Namespace: "Foo.Baz", Name: "Beta" });
        Assert.DoesNotContain(types, t => t.Name.StartsWith('<'));
    }

    [Fact]
    public void DecompileType_Should_DecompileTheWholeType_GivenJustAssemblyPathAndTypeName()
    {
        var types = CSharpDecompilerService.EnumerateTypes(_fixtures.ExplorerCodeBinary);
        var alpha = types.Single(t => t.Name == "Alpha");

        var decompiled = CSharpDecompilerService.DecompileType(_fixtures.ExplorerCodeBinary, alpha.FullTypeName);

        Assert.Contains("class Alpha", decompiled.Text);
        Assert.NotNull(decompiled.SyntaxTree);
    }
}
