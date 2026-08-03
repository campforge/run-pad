using RunPad.Contracts.IntelliSense;
using RunPad.Languages.DotnetCommon;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #62 "Go to Definition" (F12, user request 2026-07-19) — external half. Real-Roslyn
// integration test (no mocking of the compiler): the thing under test is whether
// ICSharpCode.Decompiler can locate a SPECIFIC overload's declaration line inside a type it just
// decompiled from a REAL binary, which pure-logic unit testing can't stand in for.
//
// Pass 134 (CI-cost pass) — 7 tests used to each spawn their own real `dotnet build` for one of
// only 3 distinct source snippets. An IClassFixture compiles each snippet exactly once per test class
// run and every test just reads the shared result. A later pass (test-design cleanup per
// docs/unit-test.md — real `dotnet build` child processes test the compiler/MSBuild, not RunPad's own
// code) swapped the fixture compilation itself from a real `dotnet build` to
// InProcessFixtureCompiler (in-process Roslyn CSharpCompilation.Emit) — same real IL bytes for the
// decompiler, zero child processes, no RealBuildTestCollection serialization needed anymore.
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

    // TODO.md #62 "recursive F12 inside the decompiled window" (user request 2026-07-19) —
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

    // TODO.md #62 "ILSpy assembly explorer" (user request 2026-07-19) — EnumerateTypes/DecompileType,
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

        private readonly List<string> _binaries = [];

        public ValueTask InitializeAsync()
        {
            CodeBinary = Compile(Code, "CodeFixture");
            RecursiveCodeBinary = Compile(RecursiveCode, "RecursiveCodeFixture");
            ExplorerCodeBinary = Compile(ExplorerCode, "ExplorerCodeFixture");
            return ValueTask.CompletedTask;
        }

        private string Compile(string code, string assemblyName)
        {
            var binary = InProcessFixtureCompiler.CompileToTempDll(code, assemblyName);
            _binaries.Add(binary);
            return binary;
        }

        public ValueTask DisposeAsync()
        {
            foreach (var binary in _binaries)
                File.Delete(binary);
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
