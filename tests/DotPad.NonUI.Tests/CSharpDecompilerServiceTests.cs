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
public class CSharpDecompilerServiceTests
{
    private sealed record FakeDefinitionResult(
        bool IsInSource, int Offset, string? AssemblyPath, string? ContainingTypeFullName,
        string? MemberName, IReadOnlyList<string> ParameterTypeNames, string DisplayName) : IDefinitionResult;

    private static MsBuildProcessCompiler CreateCompiler(string outputDir)
    {
        var sdk = new DotnetSdkProvider().Get().First();
        var runtimeLib = new CSharpReferenceService().GetExtensionReference();
        return new MsBuildProcessCompiler(
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
    }

    private const string Code = """
        IlDisassemblerProbe.Pick(1);
        IlDisassemblerProbe.Pick("x");
        class IlDisassemblerProbe
        {
            public static void Pick(int number) { System.Console.WriteLine(number); }
            public static void Pick(string text) { System.Console.WriteLine(text); }
        }
        """;

    [Fact]
    public async Task Decompile_Should_LocateTheExactOverload_DisambiguatedByParameterTypeNames()
    {
        var outputDir = Directory.CreateTempSubdirectory("dotpad-decompiler-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            var result = await compiler.Compilation(Code, CancellationToken.None);
            Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));

            var definition = new FakeDefinitionResult(
                IsInSource: false, Offset: 0, AssemblyPath: result.OutputBinaryFile,
                ContainingTypeFullName: "IlDisassemblerProbe", MemberName: "Pick",
                ParameterTypeNames: ["String"], DisplayName: "IlDisassemblerProbe.Pick(string)");

            var decompiled = CSharpDecompilerService.Decompile(definition);

            var lines = decompiled.Text.Split('\n');
            var line = lines[decompiled.Line - 1];
            Assert.Contains("Pick", line);
            Assert.Contains("string", line);
            Assert.DoesNotContain("int number", line);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task Decompile_Should_LocateTheType_WhenMemberNameIsNull()
    {
        var outputDir = Directory.CreateTempSubdirectory("dotpad-decompiler-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            var result = await compiler.Compilation(Code, CancellationToken.None);
            Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));

            var definition = new FakeDefinitionResult(
                IsInSource: false, Offset: 0, AssemblyPath: result.OutputBinaryFile,
                ContainingTypeFullName: "IlDisassemblerProbe", MemberName: null,
                ParameterTypeNames: [], DisplayName: "IlDisassemblerProbe");

            var decompiled = CSharpDecompilerService.Decompile(definition);

            var lines = decompiled.Text.Split('\n');
            Assert.Contains("class IlDisassemblerProbe", string.Join('\n', lines.Skip(decompiled.Line - 1).Take(3)));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

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

    [Fact]
    public async Task ResolveReferenceAtPosition_Should_ResolveACallSite_ToItsOwnDeclaration()
    {
        var outputDir = Directory.CreateTempSubdirectory("dotpad-decompiler-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            var result = await compiler.Compilation(RecursiveCode, CancellationToken.None);
            Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));

            var callerDefinition = new FakeDefinitionResult(
                IsInSource: false, Offset: 0, AssemblyPath: result.OutputBinaryFile,
                ContainingTypeFullName: "Caller", MemberName: null,
                ParameterTypeNames: [], DisplayName: "Caller");
            var caller = CSharpDecompilerService.Decompile(callerDefinition);
            Assert.NotNull(caller.SyntaxTree);

            var (line, column) = FindPosition(caller.Text, "Helper");

            var reference = CSharpDecompilerService.ResolveReferenceAtPosition(caller.SyntaxTree!, line, column);

            Assert.NotNull(reference);
            Assert.Equal("Callee", reference!.ContainingTypeFullName);
            Assert.Equal("Helper", reference.MemberName);
            Assert.Equal(result.OutputBinaryFile, reference.AssemblyPath);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task ResolveReferenceAtPosition_Result_Should_BeDirectlyConsumableByDecompile()
    {
        var outputDir = Directory.CreateTempSubdirectory("dotpad-decompiler-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            var result = await compiler.Compilation(RecursiveCode, CancellationToken.None);
            Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));

            var callerDefinition = new FakeDefinitionResult(
                IsInSource: false, Offset: 0, AssemblyPath: result.OutputBinaryFile,
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
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task ResolveReferenceAtPosition_Should_ReturnNull_WhenNothingIsResolvableThere()
    {
        var outputDir = Directory.CreateTempSubdirectory("dotpad-decompiler-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            var result = await compiler.Compilation(RecursiveCode, CancellationToken.None);
            Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));

            var callerDefinition = new FakeDefinitionResult(
                IsInSource: false, Offset: 0, AssemblyPath: result.OutputBinaryFile,
                ContainingTypeFullName: "Caller", MemberName: null,
                ParameterTypeNames: [], DisplayName: "Caller");
            var caller = CSharpDecompilerService.Decompile(callerDefinition);

            // Far past the end of the actual text — no AST node contains this position at all.
            var reference = CSharpDecompilerService.ResolveReferenceAtPosition(caller.SyntaxTree!, 9999, 1);

            Assert.Null(reference);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
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

    [Fact]
    public async Task EnumerateTypes_Should_ListTopLevelTypes_AcrossNamespaces_ExcludingCompilerGeneratedNoise()
    {
        var outputDir = Directory.CreateTempSubdirectory("dotpad-decompiler-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            var result = await compiler.Compilation(ExplorerCode, CancellationToken.None);
            Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));

            var types = CSharpDecompilerService.EnumerateTypes(result.OutputBinaryFile!);

            Assert.Contains(types, t => t is { Namespace: "Foo.Bar", Name: "Alpha" });
            Assert.Contains(types, t => t is { Namespace: "Foo.Baz", Name: "Beta" });
            Assert.DoesNotContain(types, t => t.Name.StartsWith('<'));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task DecompileType_Should_DecompileTheWholeType_GivenJustAssemblyPathAndTypeName()
    {
        var outputDir = Directory.CreateTempSubdirectory("dotpad-decompiler-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            var result = await compiler.Compilation(ExplorerCode, CancellationToken.None);
            Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));

            var types = CSharpDecompilerService.EnumerateTypes(result.OutputBinaryFile!);
            var alpha = types.Single(t => t.Name == "Alpha");

            var decompiled = CSharpDecompilerService.DecompileType(result.OutputBinaryFile!, alpha.FullTypeName);

            Assert.Contains("class Alpha", decompiled.Text);
            Assert.NotNull(decompiled.SyntaxTree);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
