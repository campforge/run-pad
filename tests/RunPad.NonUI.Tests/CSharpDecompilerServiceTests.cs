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

    // ILSpy-style tree contents: every member kind, an indexer, a generic method with a ref parameter,
    // non-public members, a nested type, a base class + interface and a derived type.
    private const string TreeCode = """
        _ = 0;

        namespace Tree
        {
            public interface IShape { double Area { get; } }
            public class Shape : IShape
            {
                private int _id;
                public double Area => 0;
                public int this[int i] => i;
                public event System.EventHandler? Changed;
                public Shape(int id) { _id = id; }
                public T Echo<T>(T value, ref int count) => value;
                internal void Hidden() { Changed?.Invoke(this, System.EventArgs.Empty); }
                public class Inner { }
            }
            public sealed class Circle : Shape { public Circle() : base(1) { } }
            public abstract class Base
            {
                private protected int _x;
                public abstract int Size { get; }
                protected internal static void Make() { }
            }
            public static class Helpers { internal static int Count; }
        }
        """;

    public sealed class CompiledFixtures : IAsyncLifetime
    {
        public string CodeBinary { get; private set; } = null!;
        public string RecursiveCodeBinary { get; private set; } = null!;
        public string ExplorerCodeBinary { get; private set; } = null!;
        public string TreeCodeBinary { get; private set; } = null!;

        private readonly List<string> _binaries = [];

        public ValueTask InitializeAsync()
        {
            CodeBinary = Compile(Code, "CodeFixture");
            RecursiveCodeBinary = Compile(RecursiveCode, "RecursiveCodeFixture");
            ExplorerCodeBinary = Compile(ExplorerCode, "ExplorerCodeFixture");
            TreeCodeBinary = Compile(TreeCode, "TreeCodeFixture");
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

    [Fact]
    public void DecompileType_Should_StartWithTheAssemblyHeaderComment()
    {
        var decompiled = CSharpDecompilerService.DecompileType(_fixtures.TreeCodeBinary, "Tree.Shape");

        Assert.StartsWith("// TreeCodeFixture, Version=", decompiled.Text);
        Assert.Contains("class Shape", decompiled.Text.Split('\n')[decompiled.Line - 1]);
    }

    [Fact]
    public void GetTypeContents_Should_ListMembers_InIlSpyOrder_WithIlSpyText()
    {
        var contents = CSharpDecompilerService.GetTypeContents(_fixtures.TreeCodeBinary, "Tree.Shape");

        Assert.Equal(
            [
                (AssemblyNodeKind.Field, "_id : int", false),
                (AssemblyNodeKind.Property, "Area : double", true),
                (AssemblyNodeKind.Property, "this[int] : int", true),
                (AssemblyNodeKind.Event, "Changed : EventHandler?", true),
                (AssemblyNodeKind.Constructor, "Shape(int)", true),
                (AssemblyNodeKind.Method, "Echo<T>(T, ref int) : T", true),
                (AssemblyNodeKind.Method, "Hidden() : void", false),
            ],
            contents.Members.Select(m => (m.Kind, m.Text, m.IsPublic)));
        Assert.Equal(["Inner"], contents.NestedTypes.Select(t => t.Text));
    }

    [Fact]
    public void GetTypeContents_Should_ReportAccess_Static_AndAbstract()
    {
        var contents = CSharpDecompilerService.GetTypeContents(_fixtures.TreeCodeBinary, "Tree.Base");

        Assert.Equal(
            [
                ("_x : int", AssemblyAccess.PrivateProtected, false, false),
                ("Size : int", AssemblyAccess.Public, false, true),
                ("Base()", AssemblyAccess.Protected, false, false),
                ("Make() : void", AssemblyAccess.ProtectedInternal, true, false),
            ],
            contents.Members.Select(m => (m.Text, m.Access, m.IsStatic, m.IsAbstract)));
    }

    [Fact]
    public void EnumerateTypes_Should_MarkAbstractClasses_AndStaticClasses_ButNotInterfaces()
    {
        var types = CSharpDecompilerService.EnumerateTypes(_fixtures.TreeCodeBinary);

        Assert.Contains(types, t => t is { Name: "Base", IsAbstract: true, IsStatic: false });
        Assert.Contains(types, t => t is { Name: "Helpers", IsAbstract: false, IsStatic: true });
        Assert.Contains(types, t => t is { Name: "IShape", IsAbstract: false, Kind: AssemblyNodeKind.Interface });
    }

    private AssemblyMemberEntry ShapeMember(string textPrefix) =>
        CSharpDecompilerService.GetTypeContents(_fixtures.TreeCodeBinary, "Tree.Shape").Members
            .Single(m => m.Text.StartsWith(textPrefix, StringComparison.Ordinal));

    private int ShapeToken() =>
        CSharpDecompilerService.EnumerateTypes(_fixtures.TreeCodeBinary).Single(t => t.Name == "Shape").MetadataToken;

    [Fact]
    public void DisassembleEntity_Should_ShowOnlyThatMethodsIl_UnderTheHeader()
    {
        var echo = ShapeMember("Echo");

        var il = IlDisassembler.DisassembleEntity(_fixtures.TreeCodeBinary, echo.MetadataToken, echo.Text);

        var lines = il.Text.Split('\n');
        Assert.StartsWith("// TreeCodeFixture, Version=", lines[0]);
        Assert.Equal("// Tree.Shape", lines[1].TrimEnd());
        Assert.Contains(".method", lines[il.Line - 1]);
        Assert.Contains("IL_0000", il.Text);
        Assert.DoesNotContain(".class", il.Text);
        Assert.Null(il.SyntaxTree);
    }

    [Fact]
    public void DisassembleEntity_Should_ShowAWholeType_AndAProperty()
    {
        var type = IlDisassembler.DisassembleEntity(_fixtures.TreeCodeBinary, ShapeToken(), "Shape");
        var area = IlDisassembler.DisassembleEntity(_fixtures.TreeCodeBinary, ShapeMember("Area").MetadataToken, "Area");

        Assert.Contains(".class", type.Text);
        Assert.Contains("Echo", type.Text);
        Assert.Contains(".property", area.Text);
        Assert.Equal("// Tree.Shape", area.Text.Split('\n')[1].TrimEnd());
    }

    [Fact]
    public void DecompileEntity_Should_HonourTheCSharpVersion()
    {
        var area = ShapeMember("Area");

        var latest = CSharpDecompilerService.DecompileEntity(
            _fixtures.TreeCodeBinary, area.MetadataToken, area.Text, ICSharpCode.Decompiler.CSharp.LanguageVersion.Latest);
        var csharp5 = CSharpDecompilerService.DecompileEntity(
            _fixtures.TreeCodeBinary, area.MetadataToken, area.Text, ICSharpCode.Decompiler.CSharp.LanguageVersion.CSharp5);

        Assert.Contains("=>", latest.Text); // expression-bodied: C# 6+
        Assert.DoesNotContain("=>", csharp5.Text);
        Assert.Contains("return", csharp5.Text);
    }

    [Fact]
    public void DecompileEntity_Should_PutTheCaretOnTheFocusMember_OfATypePage()
    {
        var echo = ShapeMember("Echo");

        var page = CSharpDecompilerService.DecompileEntity(
            _fixtures.TreeCodeBinary, ShapeToken(), "Shape", focusToken: echo.MetadataToken);

        Assert.Contains("class Shape", page.Text);
        Assert.Contains("Echo", page.Text.Split('\n')[page.Line - 1]);
    }

    [Fact]
    public void BuildSearchIndex_Should_ListTypes_NestedTypes_AndMembers_WithTheirLocation()
    {
        var index = CSharpDecompilerService.BuildSearchIndex(_fixtures.TreeCodeBinary);

        Assert.Contains(index, e => e is { Name: "Shape", Location: "Tree", Member: null });
        Assert.Contains(index, e => e is { Name: "Inner", Location: "Tree.Shape", Member: null, TypeFullName: "Tree.Shape+Inner" });
        var echo = Assert.Single(index, e => e.Name == "Echo");
        Assert.Equal("Tree.Shape", echo.Location);
        Assert.Equal("Echo<T>(T, ref int) : T", echo.Text);
        Assert.Equal(AssemblyNodeKind.Method, echo.Kind);
        // same filters as the tree: no compiler-generated names, accessors or event backing fields
        Assert.DoesNotContain(index, e => e.Name.StartsWith('<') || e.Name.StartsWith("get_"));
        Assert.Single(index, e => e.Name == "Changed");
        // a constructor is found by its type's name
        Assert.Contains(index, e => e is { Name: "Shape", Member.Kind: AssemblyNodeKind.Constructor });
    }

    [Fact]
    public void Search_Should_RankExactThenPrefixThenContains_CaseInsensitively()
    {
        var index = CSharpDecompilerService.BuildSearchIndex(_fixtures.TreeCodeBinary);

        var hits = CSharpDecompilerService.Search(index, "shape");

        Assert.Equal("Shape", hits[0].Name); // exact (the type or its constructor)
        Assert.Contains(hits, h => h.Name == "IShape"); // contains
        Assert.True(hits.ToList().FindIndex(h => h.Name == "IShape") > hits.ToList().FindLastIndex(h => h.Name == "Shape"));
        Assert.Empty(CSharpDecompilerService.Search(index, "   "));
        Assert.Empty(CSharpDecompilerService.Search(index, "NoSuchName"));
    }

    [Fact]
    public void Search_WithADot_Should_MatchTheQualifiedName()
    {
        var index = CSharpDecompilerService.BuildSearchIndex(_fixtures.TreeCodeBinary);

        var hit = Assert.Single(CSharpDecompilerService.Search(index, "Shape.Echo"));
        Assert.Equal("Echo", hit.Name);
        Assert.Contains(CSharpDecompilerService.Search(index, "Tree.Circle"), h => h is { Name: "Circle", Member: null });
        Assert.Single(CSharpDecompilerService.Search(index, "x", limit: 1));
    }

    [Fact]
    public void GetTypeContents_Should_ListBaseAndDerivedTypes()
    {
        var contents = CSharpDecompilerService.GetTypeContents(_fixtures.TreeCodeBinary, "Tree.Shape");

        Assert.Contains(contents.BaseTypes, t => t is { Text: "object", IsResolved: true, AssemblyPath: not null });
        Assert.Contains(contents.BaseTypes, t => t is { Text: "IShape", Kind: AssemblyNodeKind.Interface, AssemblyPath: null });
        var circle = Assert.Single(contents.DerivedTypes);
        Assert.Equal("Tree.Circle", circle.FullTypeName);
    }

    [Fact]
    public void DecompileMember_Should_DecompileOnlyThatMember_UnderTheDeclaringTypeHeader()
    {
        var contents = CSharpDecompilerService.GetTypeContents(_fixtures.TreeCodeBinary, "Tree.Shape");
        var echo = contents.Members.Single(m => m.Text.StartsWith("Echo"));

        var decompiled = CSharpDecompilerService.DecompileMember(_fixtures.TreeCodeBinary, echo.MetadataToken, echo.Text);

        var lines = decompiled.Text.Split('\n');
        Assert.StartsWith("// TreeCodeFixture, Version=", lines[0]);
        Assert.Equal("// Tree.Shape", lines[1].TrimEnd());
        Assert.Contains("Echo", lines[decompiled.Line - 1]);
        Assert.DoesNotContain("class Shape", decompiled.Text);
        Assert.DoesNotContain("Hidden", decompiled.Text);
        Assert.Equal(echo.Text, decompiled.Title);
    }
}
