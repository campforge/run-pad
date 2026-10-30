using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.DotnetCommon;
using Xunit;

namespace Invoklet.NonUI.Tests;

// External half. Real-Roslyn
// integration test (no mocking of the compiler): the thing under test is whether
// ICSharpCode.Decompiler can locate a SPECIFIC overload's declaration line inside a type it just
// decompiled from a REAL binary, which pure-logic unit testing can't stand in for.
//
// Pass 134 (CI-cost pass) — 7 tests used to each spawn their own real `dotnet build` for one of
// only 3 distinct source snippets. An IClassFixture compiles each snippet exactly once per test class
// run and every test just reads the shared result. A later pass (test-design cleanup:
// real `dotnet build` child processes test the compiler/MSBuild, not Invoklet's own
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

    // Recursive F12 inside the decompiled window —
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

    // EnumerateTypes/DecompileType,
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
            public class Consumer
            {
                private int _f;
                public void Caller()
                {
                    var shape = new Shape(1);
                    shape.Echo(1, ref _f);
                    _f = 2;
                    System.Func<int> read = () => _f;
                    Helper();
                }
                public static void Helper() { }
                public async System.Threading.Tasks.Task Later() { await System.Threading.Tasks.Task.Yield(); Helper(); }
                public int Read() => _f;
                public double Size() => new Circle().Area;
                // int[,] element access compiles to calls of int[,]::Set/Get — synthesized methods
                // with no metadata (and no documentation ID) behind them
                public int Grid() { var grid = new int[2, 2]; grid[0, 0] = 1; return grid[1, 1]; }
                public void Subscribe(Shape shape) { shape.Changed += OnChanged; shape.Changed -= OnChanged; }
                public int Index(Shape shape) => shape[3];
                private void OnChanged(object? sender, System.EventArgs e) { }
            }
            public class Box<T>
            {
                public void Put(T item) { }
            }
            public abstract class Animal
            {
                public abstract string Sound();
                public virtual int Legs => 4;
                public void Walk() { }
                public Animal Self() => this;
            }
            public class Dog : Animal { public override string Sound() => "woof"; }
            public class Puppy : Dog { public override string Sound() => base.Sound() + "!"; }
            public static class Listener
            {
                public static string Hear(Animal animal) => animal.Sound();   // callvirt Animal::Sound
                public static string HearDog(Dog dog) => dog.Sound();         // C# emits callvirt Animal::Sound too
                public static void GreetIt(IGreeter greeter) => greeter.Greet("x");
            }
            public sealed class Bird : Animal
            {
                public override string Sound() => "tweet";
                public override int Legs => 2;
            }
            public interface IGreeter { void Greet(string name); }
            public class Greeter : IGreeter { public void Greet(string name) { } }
            public class LoudGreeter : IGreeter { void IGreeter.Greet(string name) { } }
            public class Zoo
            {
                public Animal[] Animals = [];
                public System.Collections.Generic.List<Animal> Get(Animal first) => [first];
            }
        }
        """;

    // Analyzer edge cases: compiler-generated code of overloads (a lambda, an async state machine, a
    // local function in only one of each pair), methods that look like an interface member's
    // implementation but aren't, and a type used only as a type argument.
    private const string AnalysisCode = """
        _ = 0;

        namespace Edge
        {
            using System.Threading.Tasks;

            public class Overloads
            {
                public static void Target() { }
                public static void Other() { }
                public void Caller(int x) { Other(); }
                public void Caller(string s) { System.Action a = () => Target(); a(); }
                public async Task Later(int x) { await Task.Yield(); Other(); }
                public async Task Later(string s) { await Task.Yield(); Target(); }
                public void Local(int x) { Inner(); static void Inner() => Other(); }
                public void Local(string s) { Inner(); static void Inner() => Target(); }
            }

            public interface IWork { void M(); object Get(); }
            public class Work : IWork { public void M() { } public void M<T>() { } public object Get() => new(); }
            // the explicit Get is the implementation; the public one returns another type anyway
            public class Narrow : IWork { public void M() { } public string Get() => ""; object IWork.Get() => ""; }
            // re-implements IWork with Work's members: its own Get returns another type
            public class Reworked : Work, IWork { public new string Get() => ""; }
            public interface IStore<T> { T Take<U>(U key); }
            public class Store : IStore<string> { public string Take<U>(U key) => ""; public string Take(int key) => ""; }

            public class Marker { }
            public class MarkerUsers
            {
                public object Make() => new System.Collections.Generic.List<Marker>();
                public System.Type ListToken() => typeof(System.Collections.Generic.List<Marker>);
                public System.Type ArrayToken() => typeof(Marker[]);
                public object Empty() => System.Array.Empty<Marker>();
                public object Direct() => new Marker[1];
                public object Unrelated() => new System.Collections.Generic.List<string>();
            }
        }
        """;

    public sealed class CompiledFixtures : IAsyncLifetime
    {
        public string CodeBinary { get; private set; } = null!;
        public string RecursiveCodeBinary { get; private set; } = null!;
        public string ExplorerCodeBinary { get; private set; } = null!;
        public string TreeCodeBinary { get; private set; } = null!;
        public string AnalysisCodeBinary { get; private set; } = null!;

        private readonly List<string> _binaries = [];

        public ValueTask InitializeAsync()
        {
            CodeBinary = Compile(Code, "CodeFixture");
            RecursiveCodeBinary = Compile(RecursiveCode, "RecursiveCodeFixture");
            ExplorerCodeBinary = Compile(ExplorerCode, "ExplorerCodeFixture");
            TreeCodeBinary = Compile(TreeCode, "TreeCodeFixture", TreeResources());
            AnalysisCodeBinary = Compile(AnalysisCode, "AnalysisCodeFixture");
            return ValueTask.CompletedTask;
        }

        // The Resources node's three kinds of page: text, a .resources table, binary.
        private static IReadOnlyList<(string Name, byte[] Data)> TreeResources()
        {
            using var table = new MemoryStream();
            using (var writer = new System.Resources.ResourceWriter(table))
            {
                writer.AddResource("Greeting", "Hello\nworld");
                writer.AddResource("Answer", 42);
                writer.AddResource("Blob", new byte[] { 1, 2, 3 });
            }
            return
            [
                ("notes.txt", System.Text.Encoding.UTF8.GetBytes("first line\nsecond line")),
                ("Tree.Strings.resources", table.ToArray()),
                ("data.bin", Enumerable.Range(0, 40).Select(i => (byte)i).ToArray()),
            ];
        }

        private string Compile(string code, string assemblyName, IReadOnlyList<(string Name, byte[] Data)>? resources = null)
        {
            var binary = InProcessFixtureCompiler.CompileToTempDll(code, assemblyName, resources);
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
    public void FindReferenceSpanAtPosition_Should_SpanTheIdentifier_OnlyWhereF12WouldGoSomewhere()
    {
        var caller = CSharpDecompilerService.DecompileType(_fixtures.RecursiveCodeBinary, "Caller");
        var (line, column) = FindPosition(caller.Text, "Helper");

        var span = CSharpDecompilerService.FindReferenceSpanAtPosition(caller.SyntaxTree!, line, column + 2);

        Assert.Equal(new ReferenceSpan(line, column, line, column + "Helper".Length), span);
        var (keywordLine, keywordColumn) = FindPosition(caller.Text, "public");
        Assert.Null(CSharpDecompilerService.FindReferenceSpanAtPosition(caller.SyntaxTree!, keywordLine, keywordColumn));
        Assert.Null(CSharpDecompilerService.FindReferenceSpanAtPosition(caller.SyntaxTree!, 9999, 1));
    }

    [Fact]
    public void ResolveDefinitionTokens_Should_GiveTheTypeAndTheChosenOverloadsTokens()
    {
        var definition = new FakeDefinitionResult(
            IsInSource: false, Offset: 0, AssemblyPath: _fixtures.TreeCodeBinary,
            ContainingTypeFullName: "Tree.Shape", MemberName: "Echo",
            ParameterTypeNames: ["T", "Int32"], DisplayName: "Tree.Shape.Echo");

        var tokens = CSharpDecompilerService.ResolveDefinitionTokens(definition);

        Assert.Equal(new DefinitionTokens(ShapeToken(), ShapeMember("Echo").MetadataToken), tokens);
        Assert.Equal(
            new DefinitionTokens(ShapeToken(), null),
            CSharpDecompilerService.ResolveDefinitionTokens(definition with { MemberName = null }));
        Assert.Null(CSharpDecompilerService.ResolveDefinitionTokens(definition with { ContainingTypeFullName = "Tree.Nope" }));
    }

    // F12 on a property/event/indexer use must land on the property/event itself — the explorer's tree
    // lists those, not their get_/set_/add_/remove_ accessors.
    [Theory]
    [InlineData("Size", "Area", "Area")]
    [InlineData("Subscribe", "Changed +=", "Changed")]
    [InlineData("Subscribe", "Changed -=", "Changed")]
    [InlineData("Index", "shape[", "Item")]
    public void ResolveReferenceAtPosition_OnAnAccessorUse_Should_ResolveToTheOwningPropertyOrEvent(
        string method, string needle, string expectedMember)
    {
        var page = CSharpDecompilerService.DecompileMember(_fixtures.TreeCodeBinary, Token("Tree.Consumer", method), method);
        var (line, column) = FindPosition(page.Text, needle);
        if (needle.EndsWith('[')) column += needle.Length; // inside the brackets of the indexer access

        var reference = CSharpDecompilerService.ResolveReferenceAtPosition(page.SyntaxTree!, line, column + 1);

        Assert.NotNull(reference);
        Assert.Equal("Tree.Shape", reference!.ContainingTypeFullName);
        Assert.Equal(expectedMember, reference.MemberName);
        var owner = CSharpDecompilerService.GetTypeContents(_fixtures.TreeCodeBinary, "Tree.Shape").Members
            .Single(m => m.Text.StartsWith(expectedMember == "Item" ? "this[" : expectedMember, StringComparison.Ordinal));
        Assert.Equal(owner.MetadataToken, reference.MetadataToken); // a row the tree has
    }

    // F12 from the script: Roslyn names an indexer "this[]" and may hand over an accessor (get_Area) —
    // both must land on the indexer/property row and line, not on the type.
    [Theory]
    [InlineData("this[]", "this[")]
    [InlineData("get_Area", "Area :")]
    [InlineData("add_Changed", "Changed :")]
    public void ResolveDefinitionTokens_Should_MapRoslynIndexerNames_AndAccessors_ToTheTreesRow(
        string roslynMemberName, string rowTextPrefix)
    {
        var definition = new FakeDefinitionResult(
            IsInSource: false, Offset: 0, AssemblyPath: _fixtures.TreeCodeBinary,
            ContainingTypeFullName: "Tree.Shape", MemberName: roslynMemberName,
            ParameterTypeNames: [], DisplayName: "Tree.Shape." + roslynMemberName);

        var tokens = CSharpDecompilerService.ResolveDefinitionTokens(definition);
        var page = CSharpDecompilerService.Decompile(definition);

        Assert.Equal(ShapeMember(rowTextPrefix).MetadataToken, tokens!.MemberToken);
        Assert.DoesNotContain("class Shape", page.Text.Split('\n')[page.Line - 1]); // the caret is on the member
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

    [Theory]
    [InlineData("Box.Put")]
    [InlineData("Box<T>.Put")]
    [InlineData("Tree.Box.Put")]
    public void Search_WithADot_Should_FindMembersOfGenericTypes_WithOrWithoutTypeArguments(string query)
    {
        var index = CSharpDecompilerService.BuildSearchIndex(_fixtures.TreeCodeBinary);

        var hit = Assert.Single(CSharpDecompilerService.Search(index, query));
        Assert.Equal("Put", hit.Name);
        Assert.Equal("Tree.Box<T>", hit.Location);
    }

    [Fact]
    public void DescribeAssembly_Should_GiveTheName_AndResolveTheReferences()
    {
        var description = CSharpDecompilerService.DescribeAssembly(_fixtures.TreeCodeBinary);

        Assert.Equal("TreeCodeFixture", description.Name);
        Assert.StartsWith("TreeCodeFixture, Version=", description.FullName);
        Assert.NotNull(description.Version);
        var runtime = Assert.Single(description.References, r => r.Name == "System.Runtime");
        Assert.StartsWith("System.Runtime, Version=", runtime.FullName);
        Assert.True(File.Exists(runtime.ResolvedPath), runtime.ResolvedPath);
        Assert.Equal(description.References.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase), description.References);
    }

    // ── Resources ──

    [Fact]
    public void DescribeAssembly_Should_ListTheEmbeddedResources_SortedByName()
    {
        var resources = CSharpDecompilerService.DescribeAssembly(_fixtures.TreeCodeBinary).Resources;

        Assert.Equal(["data.bin", "notes.txt", "Tree.Strings.resources"], resources.Select(r => r.Name));
        Assert.All(resources, r => Assert.True(r is { IsEmbedded: true, IsPublic: true }));
        Assert.Equal(40, resources.Single(r => r.Name == "data.bin").Size);
    }

    [Fact]
    public void DescribeResource_Should_ShowText_AsText()
    {
        var page = CSharpDecompilerService.DescribeResource(_fixtures.TreeCodeBinary, "notes.txt");

        Assert.Equal("// notes.txt (22 bytes)\n\nfirst line\nsecond line", page.Text);
        Assert.Equal("notes.txt", page.Title);
    }

    [Fact]
    public void DescribeResource_Should_ShowAResourcesFile_AsSortedKeyValueLines()
    {
        var text = CSharpDecompilerService.DescribeResource(_fixtures.TreeCodeBinary, "Tree.Strings.resources").Text;

        Assert.Contains("// 3 entries\n", text);
        Assert.Contains("Answer = 42 (Int32)\nBlob = byte[3]\nGreeting = \"Hello\\nworld\"\n", text);
    }

    [Fact]
    public void DescribeResource_Should_ShowBinaryData_AsAHexDump()
    {
        var lines = CSharpDecompilerService.DescribeResource(_fixtures.TreeCodeBinary, "data.bin").Text.Split('\n');

        Assert.Equal("// data.bin (40 bytes)", lines[0]);
        Assert.Equal("00000000  00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  |................|", lines[2]);
        Assert.StartsWith("00000020  20 21 22 23 24 25 26 27", lines[4]);
        Assert.EndsWith("| !\"#$%&'|", lines[4]);
    }

    [Fact]
    public void DescribeResource_Should_SayWhenTheResourceIsMissing()
    {
        Assert.StartsWith("// Resource 'nope' was not found", CSharpDecompilerService.DescribeResource(_fixtures.TreeCodeBinary, "nope").Text);
    }

    [Fact]
    public void TheAssemblyToken_Should_ShowTheAttributesInCSharp_AndTheManifestInIl()
    {
        var csharp = CSharpDecompilerService.DecompileEntity(_fixtures.TreeCodeBinary, AssemblyDescription.Token, "asm");
        var il = IlDisassembler.DisassembleEntity(_fixtures.TreeCodeBinary, AssemblyDescription.Token, "asm");

        Assert.StartsWith("// TreeCodeFixture, Version=", csharp.Text);
        Assert.Contains("[assembly:", csharp.Text);
        Assert.Contains(".assembly extern System.Runtime", il.Text);
        Assert.Contains(".assembly TreeCodeFixture", il.Text);
        Assert.Contains(".module", il.Text);
    }

    // ── Analyze ──

    private int Token(string typeName, string textPrefix) =>
        CSharpDecompilerService.GetTypeContents(_fixtures.TreeCodeBinary, typeName).Members
            .Single(m => m.Text.StartsWith(textPrefix, StringComparison.Ordinal)).MetadataToken;

    private IReadOnlyList<string> Analyze(AnalysisKind kind, int token) =>
        CSharpDecompilerService.Analyze(kind, _fixtures.TreeCodeBinary, token, [_fixtures.TreeCodeBinary])
            .Select(r => $"{r.Location}.{r.Text}").ToList();

    [Fact]
    public void UsedBy_OfAMethod_Should_ListItsCallers_CountingAStateMachineAsItsAsyncMethod()
    {
        Assert.Equal(["Tree.Consumer.Caller() : void"], Analyze(AnalysisKind.UsedBy, Token("Tree.Shape", "Echo")));
        // Later() is async: the call sits in its compiler-generated state machine's MoveNext
        Assert.Equal(
            ["Tree.Consumer.Caller() : void", "Tree.Consumer.Later() : Task"],
            Analyze(AnalysisKind.UsedBy, Token("Tree.Consumer", "Helper")));
    }

    [Fact]
    public void UsedBy_OfAProperty_Should_ListTheCallersOfItsAccessors()
    {
        Assert.Equal(["Tree.Consumer.Size() : double"], Analyze(AnalysisKind.UsedBy, Token("Tree.Shape", "Area")));
    }

    [Fact]
    public void ReadBy_And_AssignedBy_OfAField_Should_CountALambdaAsItsMethod()
    {
        // Caller reads _f through `ref _f` and inside its lambda; Read() reads it directly
        Assert.Equal(
            ["Tree.Consumer.Caller() : void", "Tree.Consumer.Read() : int"],
            Analyze(AnalysisKind.ReadBy, Token("Tree.Consumer", "_f")));
        Assert.Equal(["Tree.Consumer.Caller() : void"], Analyze(AnalysisKind.AssignedBy, Token("Tree.Consumer", "_f")));
    }

    [Fact]
    public void InstantiatedBy_And_UsedBy_OfAType_Should_LeaveOutTheTypesOwnCode()
    {
        Assert.Equal(["Tree.Consumer.Caller() : void"], Analyze(AnalysisKind.InstantiatedBy, ShapeToken()));
        // Circle's constructor calls Shape's (`call`, not `newobj`): a use, not an instantiation;
        // Size() calls Shape::get_Area through a Circle — a use of Shape's member
        Assert.Equal(
            [
                "Tree.Circle.Circle()", "Tree.Consumer.Caller() : void", "Tree.Consumer.Index(Shape) : int",
                "Tree.Consumer.Size() : double", "Tree.Consumer.Subscribe(Shape) : void",
            ],
            Analyze(AnalysisKind.UsedBy, ShapeToken()));
    }

    [Fact]
    public void Uses_OfAMethod_Should_ListWhatItsBody_AndItsLambdas_ReferTo()
    {
        var uses = Analyze(AnalysisKind.Uses, Token("Tree.Consumer", "Caller"));

        Assert.Contains("Tree.Shape.Shape(int)", uses);
        Assert.Contains("Tree.Shape.Echo<T>(T, ref int) : T", uses);
        Assert.Contains("Tree.Consumer._f : int", uses);
        Assert.Contains("Tree.Consumer.Helper() : void", uses);
        Assert.DoesNotContain("Tree.Consumer.Caller() : void", uses); // not itself (its lambda maps to it)

        // Later()'s own body only builds its state machine; Helper() is called from MoveNext
        Assert.Contains("Tree.Consumer.Helper() : void", Analyze(AnalysisKind.Uses, Token("Tree.Consumer", "Later")));
    }

    [Fact]
    public void Analyze_Should_SkipSynthesizedArrayMethods_InsteadOfFailing()
    {
        // every UsedBy scan reads Grid()'s body, whose int[,]::Set/Get calls have no documentation ID
        Assert.Equal(["Tree.Consumer.Caller() : void"], Analyze(AnalysisKind.UsedBy, Token("Tree.Shape", "Echo")));

        var uses = Analyze(AnalysisKind.Uses, Token("Tree.Consumer", "Grid"));
        Assert.DoesNotContain(uses, u => u.Contains("Set(") || u.Contains("Get("));
    }

    private AssemblyMemberEntry Member(string typeName, string textPrefix) =>
        CSharpDecompilerService.GetTypeContents(_fixtures.TreeCodeBinary, typeName).Members
            .Single(m => m.Text.StartsWith(textPrefix, StringComparison.Ordinal));

    private int TypeToken(string name) =>
        CSharpDecompilerService.EnumerateTypes(_fixtures.TreeCodeBinary).Single(t => t.Name == name).MetadataToken;

    [Fact]
    public void AnalysesFor_Should_OfferIlSpysQueriesPerKind()
    {
        Assert.Equal([AnalysisKind.UsedBy, AnalysisKind.Uses], CSharpDecompilerService.AnalysesFor(AssemblyNodeKind.Method));
        Assert.Equal([AnalysisKind.ReadBy, AnalysisKind.AssignedBy], CSharpDecompilerService.AnalysesFor(AssemblyNodeKind.Field));
        Assert.Equal(
            [AnalysisKind.UsedBy, AnalysisKind.InstantiatedBy, AnalysisKind.ExposedBy],
            CSharpDecompilerService.AnalysesFor(AssemblyNodeKind.Class));
        Assert.Equal(
            [AnalysisKind.UsedBy, AnalysisKind.ImplementedBy, AnalysisKind.ExposedBy],
            CSharpDecompilerService.AnalysesFor(AssemblyNodeKind.Interface));
        Assert.Empty(CSharpDecompilerService.AnalysesFor(AssemblyNodeKind.Namespace));
    }

    [Fact]
    public void AnalysesFor_Should_OfferOverriddenBy_OnlyForOverridableMembers_AndImplementedBy_ForInterfaceMembers()
    {
        var walk = Member("Tree.Animal", "Walk");
        var sound = Member("Tree.Animal", "Sound");
        var greet = Member("Tree.IGreeter", "Greet");
        Assert.False(walk.IsOverridable);
        Assert.True(sound.IsOverridable);
        Assert.False(Member("Tree.Bird", "Sound").IsOverridable); // override in a sealed class

        Assert.Equal([AnalysisKind.UsedBy, AnalysisKind.Uses], CSharpDecompilerService.AnalysesFor(walk.Kind, walk));
        Assert.Equal(
            [AnalysisKind.UsedBy, AnalysisKind.Uses, AnalysisKind.OverriddenBy],
            CSharpDecompilerService.AnalysesFor(sound.Kind, sound));
        Assert.Equal(
            [AnalysisKind.UsedBy, AnalysisKind.Uses, AnalysisKind.ImplementedBy],
            CSharpDecompilerService.AnalysesFor(greet.Kind, greet));
    }

    [Fact]
    public void AnExplicitInterfaceImplementation_Should_BeNamedLikeIlSpy_InterfaceDotMember()
    {
        var member = Assert.Single(CSharpDecompilerService.GetTypeContents(_fixtures.TreeCodeBinary, "Tree.LoudGreeter").Members,
            m => m.Kind == AssemblyNodeKind.Method);

        Assert.Equal("IGreeter.Greet(string) : void", member.Text);
        Assert.Equal(AssemblyAccess.Private, member.Access); // greyed, with the lock overlay
    }

    private IReadOnlyList<string> UsedBy(int token, bool includeVirtualCalls) =>
        CSharpDecompilerService.Analyze(
                AnalysisKind.UsedBy, _fixtures.TreeCodeBinary, token, [_fixtures.TreeCodeBinary],
                includeVirtualCalls: includeVirtualCalls)
            .Select(r => $"{r.Location}.{r.Text}").ToList();

    [Fact]
    public void UsedBy_OfAnOverride_Should_CountVirtualCallsOfItsBase_OnlyWhenAsked()
    {
        var dogSound = Member("Tree.Dog", "Sound").MetadataToken;

        // without: only Puppy's `base.Sound()` — a direct `call Dog::Sound`
        Assert.Equal(["Tree.Puppy.Sound() : string"], UsedBy(dogSound, includeVirtualCalls: false));
        // with: callvirt Animal::Sound may reach Dog.Sound, whatever the receiver's static type
        Assert.Equal(
            ["Tree.Listener.Hear(Animal) : string", "Tree.Listener.HearDog(Dog) : string", "Tree.Puppy.Sound() : string"],
            UsedBy(dogSound, includeVirtualCalls: true));
    }

    [Fact]
    public void UsedBy_WithVirtualCalls_Should_NotCountADirectCallOfTheBase()
    {
        // Puppy's base.Sound() is `call Dog::Sound`: it can't reach Bird.Sound
        var birdSound = Member("Tree.Bird", "Sound").MetadataToken;

        var users = UsedBy(birdSound, includeVirtualCalls: true);

        Assert.Contains("Tree.Listener.Hear(Animal) : string", users);
        Assert.DoesNotContain("Tree.Puppy.Sound() : string", users);
    }

    [Fact]
    public void UsedBy_OfAnInterfaceImplementation_Should_CountCallsThroughTheInterface_WhenAsked()
    {
        var implicitGreet = Member("Tree.Greeter", "Greet(").MetadataToken; // not the Greeter() constructor
        var explicitGreet = Member("Tree.LoudGreeter", "IGreeter.Greet").MetadataToken;

        Assert.Empty(UsedBy(implicitGreet, includeVirtualCalls: false));
        Assert.Equal(["Tree.Listener.GreetIt(IGreeter) : void"], UsedBy(implicitGreet, includeVirtualCalls: true));
        Assert.Equal(["Tree.Listener.GreetIt(IGreeter) : void"], UsedBy(explicitGreet, includeVirtualCalls: true));
    }

    [Fact]
    public void OverriddenBy_Should_ListTheOverridingMethods_AndProperties()
    {
        Assert.Equal(
            ["Tree.Bird.Sound() : string", "Tree.Dog.Sound() : string"],
            Analyze(AnalysisKind.OverriddenBy, Member("Tree.Animal", "Sound").MetadataToken));
        Assert.Equal(["Tree.Bird.Legs : int"], Analyze(AnalysisKind.OverriddenBy, Member("Tree.Animal", "Legs").MetadataToken));
    }

    [Fact]
    public void ImplementedBy_Should_FindImplicitAndExplicitImplementations_AndImplementingTypes()
    {
        Assert.Equal(
            ["Tree.Greeter.Greet(string) : void", "Tree.LoudGreeter.IGreeter.Greet(string) : void"],
            Analyze(AnalysisKind.ImplementedBy, Member("Tree.IGreeter", "Greet").MetadataToken));

        // Circle implements IShape through its base class Shape
        Assert.Equal(["Tree.Circle", "Tree.Shape"], Analyze(AnalysisKind.ImplementedBy, TypeToken("IShape")));
    }

    [Fact]
    public void ExposedBy_Should_FindTheTypeInsideArraysGenericsAndParameters()
    {
        Assert.Equal(
            [
                "Tree.Animal.Self() : Animal", "Tree.Listener.Hear(Animal) : string",
                "Tree.Zoo.Animals : Animal[]", "Tree.Zoo.Get(Animal) : List<Animal>",
            ],
            Analyze(AnalysisKind.ExposedBy, TypeToken("Animal")));
    }

    private int EdgeToken(string typeName, string text) =>
        CSharpDecompilerService.GetTypeContents(_fixtures.AnalysisCodeBinary, typeName).Members
            .Single(m => m.Text == text).MetadataToken;

    private IReadOnlyList<string> AnalyzeEdge(AnalysisKind kind, int token) =>
        CSharpDecompilerService.Analyze(kind, _fixtures.AnalysisCodeBinary, token, [_fixtures.AnalysisCodeBinary])
            .Select(r => $"{r.Location}.{r.Text}").ToList();

    [Fact]
    public void UsedBy_Should_CountALambda_StateMachine_OrLocalFunction_AsTheOverloadItBelongsTo()
    {
        // each overload pair has the same name: only the IL references tell them apart
        Assert.Equal(
            [
                "Edge.Overloads.Caller(string) : void", "Edge.Overloads.Later(string) : Task",
                "Edge.Overloads.Local(string) : void",
            ],
            AnalyzeEdge(AnalysisKind.UsedBy, EdgeToken("Edge.Overloads", "Target() : void")));
        Assert.Equal(
            [
                "Edge.Overloads.Caller(int) : void", "Edge.Overloads.Later(int) : Task",
                "Edge.Overloads.Local(int) : void",
            ],
            AnalyzeEdge(AnalysisKind.UsedBy, EdgeToken("Edge.Overloads", "Other() : void")));
    }

    [Theory]
    [InlineData("Caller(string) : void", "Caller(int) : void")]
    [InlineData("Later(string) : Task", "Later(int) : Task")]
    [InlineData("Local(string) : void", "Local(int) : void")]
    public void Uses_Should_ReadOnlyTheGeneratedCodeOfThatOverload(string withGeneratedCall, string withoutIt)
    {
        var uses = AnalyzeEdge(AnalysisKind.Uses, EdgeToken("Edge.Overloads", withGeneratedCall));
        Assert.Contains("Edge.Overloads.Target() : void", uses);
        Assert.DoesNotContain("Edge.Overloads.Other() : void", uses);

        var otherUses = AnalyzeEdge(AnalysisKind.Uses, EdgeToken("Edge.Overloads", withoutIt));
        Assert.Contains("Edge.Overloads.Other() : void", otherUses);
        Assert.DoesNotContain("Edge.Overloads.Target() : void", otherUses);
    }

    [Fact]
    public void ImplementedBy_Should_LeaveOutMembersWithAnotherArityOrReturnType_OrHiddenByAnExplicitImplementation()
    {
        // not Work.M<T>()
        Assert.Equal(
            ["Edge.Narrow.M() : void", "Edge.Work.M() : void"],
            AnalyzeEdge(AnalysisKind.ImplementedBy, EdgeToken("Edge.IWork", "M() : void")));
        // not Narrow.Get() : string (the explicit one implements it), nor Reworked.Get() : string
        Assert.Equal(
            ["Edge.Narrow.IWork.Get() : object", "Edge.Work.Get() : object"],
            AnalyzeEdge(AnalysisKind.ImplementedBy, EdgeToken("Edge.IWork", "Get() : object")));
        // IStore<string>.Take<U>(U) : string — substituted, matched by generic arity
        Assert.Equal(
            ["Edge.Store.Take<U>(U) : string"],
            AnalyzeEdge(AnalysisKind.ImplementedBy, EdgeToken("Edge.IStore`1", "Take<U>(U) : T")));
    }

    [Fact]
    public void UsedBy_OfAType_Should_FindItAsATypeArgument_OfATypeOrAGenericMethod()
    {
        var marker = CSharpDecompilerService.EnumerateTypes(_fixtures.AnalysisCodeBinary)
            .Single(t => t.Name == "Marker").MetadataToken;

        Assert.Equal(
            [
                "Edge.MarkerUsers.ArrayToken() : Type", "Edge.MarkerUsers.Direct() : object",
                "Edge.MarkerUsers.Empty() : object", "Edge.MarkerUsers.ListToken() : Type",
                "Edge.MarkerUsers.Make() : object",
            ],
            AnalyzeEdge(AnalysisKind.UsedBy, marker));
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
