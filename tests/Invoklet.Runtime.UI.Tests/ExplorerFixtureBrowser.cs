using ICSharpCode.Decompiler.CSharp;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.DotnetCommon;

namespace Invoklet.Runtime.UI.Tests;

// An in-memory assembly for the windows that read assemblies: the same shape as the one
// AssemblyExplorerWindowTests in Invoklet.IntegrationTests compiles (Foo.Bar.Alpha with Run(),
// Gamma : Alpha calling it, Foo.Baz with an abstract Delta and its Epsilon). Reading real assemblies is
// tested once, in CSharpDecompilerServiceTests; a window test only needs to know what the window does
// with the answers.
internal sealed class ExplorerFixtureBrowser : IAssemblyBrowser
{
    public const string Path = "/fixture/ExplorerFixture.dll";
    public const string RuntimePath = "/fixture/System.Runtime.dll";
    public const string SecondPath = "/fixture/Second.dll";

    private sealed record Member(AssemblyMemberEntry Entry, string Source, string Il);

    private sealed record TypeDef(
        AssemblyTypeEntry Entry, string? Base, string Source, string Il, params Member[] Members);

    private static AssemblyMemberEntry Method(string text, int token, bool isStatic = false, bool isAbstract = false,
        bool isOverridable = false, AssemblyAccess access = AssemblyAccess.Public) =>
        new(AssemblyNodeKind.Method, text, access, token, isStatic, isAbstract, isOverridable);

    private static AssemblyTypeEntry Type(string ns, string name, int token, bool isStatic = false, bool isAbstract = false) =>
        new(ns, name, ns.Length > 0 ? ns + "." + name : name, MetadataToken: token, IsStatic: isStatic, IsAbstract: isAbstract);

    private static Member M(AssemblyMemberEntry entry, string source, string il) => new(entry, source, il);

    private static readonly TypeDef[] Types =
    [
        new(Type("", "Program", 0x02000002), null, "internal class Program\n{\n}\n", ".class private Program\n", []),
        new(Type("Foo.Bar", "Alpha", 0x02000003), null,
            "public class Alpha\n{\n    private int _count;\n\n    public void Run()\n    {\n        _count++;\n    }\n}\n",
            ".class public auto ansi Alpha\n{\n  .method public hidebysig instance void Run() cil managed\n  {\n    IL_0000: ret\n  }\n}\n",
            M(new(AssemblyNodeKind.Field, "_count : int", AssemblyAccess.Private, 0x04000001),
                "private int _count;", ".field private int32 _count"),
            M(new(AssemblyNodeKind.Constructor, "Alpha()", AssemblyAccess.Public, 0x06000010),
                "public Alpha()\n{\n}\n", ".method public hidebysig specialname rtspecialname instance void .ctor() cil managed\n{\n}\n"),
            M(Method("Run() : void", 0x06000001), "public void Run()\n{\n    _count++;\n}\n",
                ".method public hidebysig instance void Run() cil managed\n{\n  IL_0000: ret\n}\n")),
        new(Type("Foo.Bar", "Gamma", 0x02000004), "Foo.Bar.Alpha",
            "public class Gamma : Alpha\n{\n    public void Call()\n    {\n        Run();\n    }\n}\n",
            ".class public auto ansi Gamma extends Alpha\n{\n  .method public hidebysig instance void Call() cil managed\n  {\n    IL_0000: ret\n  }\n}\n",
            M(Method("Call() : void", 0x06000002), "public void Call()\n{\n    Run();\n}\n",
                ".method public hidebysig instance void Call() cil managed\n{\n  IL_0000: ret\n}\n")),
        new(Type("Foo.Baz", "Beta", 0x02000005), null, "public class Beta\n{\n}\n", ".class public auto ansi Beta\n", []),
        new(Type("Foo.Baz", "Delta", 0x02000006, isAbstract: true), null,
            "public abstract class Delta\n{\n    public static int Zero;\n\n    public abstract void Go();\n}\n",
            ".class public abstract auto ansi Delta\n{\n  .method public hidebysig abstract virtual instance void Go() cil managed\n  {\n  }\n}\n",
            M(new(AssemblyNodeKind.Field, "Zero : int", AssemblyAccess.Public, 0x04000002, IsStatic: true),
                "public static int Zero;", ".field public static int32 Zero"),
            M(new(AssemblyNodeKind.Constructor, "Delta()", AssemblyAccess.Protected, 0x06000011),
                "protected Delta()\n{\n}\n", ".method family hidebysig specialname rtspecialname instance void .ctor() cil managed\n{\n}\n"),
            M(Method("Go() : void", 0x06000003, isAbstract: true, isOverridable: true), "public abstract void Go();",
                ".method public hidebysig abstract virtual instance void Go() cil managed\n{\n}\n")),
        new(Type("Foo.Baz", "Epsilon", 0x02000007), "Foo.Baz.Delta",
            "public class Epsilon : Delta\n{\n    public override void Go()\n    {\n    }\n}\n",
            ".class public auto ansi Epsilon extends Delta\n{\n  .method public hidebysig virtual instance void Go() cil managed\n  {\n    IL_0000: ret\n  }\n}\n",
            M(Method("Go() : void", 0x06000004, isOverridable: true), "public override void Go()\n{\n}\n",
                ".method public hidebysig virtual instance void Go() cil managed\n{\n  IL_0000: ret\n}\n")),
        new(Type("Foo.Baz", "DeltaUser", 0x02000008, isStatic: true, isAbstract: true), null,
            "public static class DeltaUser\n{\n    public static void Use(Delta delta)\n    {\n        delta.Go();\n    }\n}\n",
            ".class public abstract sealed auto ansi DeltaUser\n{\n  .method public hidebysig static void Use(class Delta delta) cil managed\n  {\n    IL_0000: ret\n  }\n}\n",
            M(Method("Use(Delta) : void", 0x06000005, isStatic: true), "public static void Use(Delta delta)\n{\n    delta.Go();\n}\n",
                ".method public hidebysig static void Use(class Delta delta) cil managed\n{\n  IL_0000: ret\n}\n")),
    ];

    // A page's work runs on a worker like the real reader's, so a test can hold an answer back.
    public Action<int>? OnDecompile { get; set; }

    // System.Object as the base types list shows it: a type of another assembly, written as its keyword.
    private static readonly AssemblyTypeEntry ObjectType = new("System", "Object", "System.Object",
        DisplayName: "object", AssemblyPath: RuntimePath, MetadataToken: 0x02000100);

    private static readonly TypeDef[] RuntimeTypes =
        [new(ObjectType with { AssemblyPath = null }, null, "public class Object\n{\n}\n", ".class public Object\n",
            M(Method("ToString() : string", 0x06000200), "public virtual string ToString();", ".method public virtual string ToString()\n"))];

    private TypeDef? TypeOf(int token) => Types.Concat(RuntimeTypes).FirstOrDefault(t => t.Entry.MetadataToken == token);

    private (TypeDef Type, Member Member)? MemberOf(int token) =>
        Types.SelectMany(t => t.Members.Select(m => (t, m))).Cast<(TypeDef, Member)?>()
            .FirstOrDefault(x => x!.Value.Item2.Entry.MetadataToken == token);

    private static void RequireAssembly(string assemblyPath)
    {
        if (assemblyPath.Contains("notadll", StringComparison.Ordinal))
            throw new BadImageFormatException("Not a .NET assembly.");
    }

    public AssemblyDescription DescribeAssembly(string assemblyPath)
    {
        RequireAssembly(assemblyPath);
        return assemblyPath switch
        {
            RuntimePath => new("System.Runtime", "System.Runtime, Version=10.0.0.0", new Version(10, 0, 0, 0), [], []),
            SecondPath => new("Second", "Second, Version=1.0.0.0", new Version(1, 0, 0, 0), [], []),
            _ => new("ExplorerFixture", "ExplorerFixture, Version=1.0.0.0", new Version(1, 0, 0, 0),
                [new("System.Runtime", "System.Runtime, Version=10.0.0.0", RuntimePath),
                 new("Missing.Library", "Missing.Library, Version=1.0.0.0", null)], []),
        };
    }

    public IReadOnlyList<AssemblyTypeEntry> EnumerateTypes(string assemblyPath)
    {
        RequireAssembly(assemblyPath);
        return TypesOf(assemblyPath).Select(t => t.Entry).ToList();
    }

    // The runtime assembly has no Foo.* types, so a search for them finds them in one assembly only.
    private static TypeDef[] TypesOf(string assemblyPath) => assemblyPath == RuntimePath ? RuntimeTypes : Types;

    public AssemblyTypeContents GetTypeContents(string assemblyPath, string typeFullName)
    {
        var type = Types.Concat(RuntimeTypes).First(t => t.Entry.FullTypeName == typeFullName);
        var bases = type.Base is null ? [ObjectType] : Types.Where(t => t.Entry.FullTypeName == type.Base).Select(t => t.Entry).ToList();
        var derived = Types.Where(t => t.Base == typeFullName).Select(t => t.Entry).ToList();
        return new(bases, derived, [], type.Members.Select(m => m.Entry).ToList());
    }

    public DecompiledMemberResult DecompileEntity(
        string assemblyPath, int metadataToken, string title, LanguageVersion? languageVersion, int focusToken)
    {
        OnDecompile?.Invoke(metadataToken);
        if (metadataToken == AssemblyDescription.Token)
            return new("// ExplorerFixture, Version=1.0.0.0\n[assembly: AssemblyVersion(\"1.0.0.0\")]\n", 1, title);
        if (MemberOf(metadataToken) is { } found)
        {
            var header = "// " + found.Type.Entry.FullTypeName + "\n";
            return new(header + found.Member.Source, 2, title);
        }
        var type = TypeOf(metadataToken)!;
        var text = "// " + type.Entry.FullTypeName + "\n" + type.Source;
        var line = focusToken != 0 && MemberOf(focusToken) is { } focus
            ? text.Split('\n').ToList().FindIndex(l => l.Contains(focus.Member.Entry.Text.Split('(')[0] + "(") || l.Contains(focus.Member.Entry.Text.Split(' ')[0] + ";")) + 1
            : 1;
        return new(text, Math.Max(1, line), title);
    }

    public DecompiledMemberResult DisassembleEntity(string assemblyPath, int metadataToken, string title)
    {
        OnDecompile?.Invoke(metadataToken);
        if (metadataToken == AssemblyDescription.Token)
            return new("// ExplorerFixture\n.assembly ExplorerFixture\n{\n}\n", 1, title);
        if (MemberOf(metadataToken) is { } found)
            return new("// " + found.Type.Entry.FullTypeName + "\n" + found.Member.Il, 2, title);
        return new("// " + TypeOf(metadataToken)!.Entry.FullTypeName + "\n" + TypeOf(metadataToken)!.Il, 1, title);
    }

    public DecompiledMemberResult DescribeResource(string assemblyPath, string resourceName) => new("", 1, resourceName);

    public IReadOnlyList<AssemblySearchEntry> BuildSearchIndex(string assemblyPath) =>
        TypesOf(assemblyPath).SelectMany(t => t.Members.Select(m => new AssemblySearchEntry(
                m.Entry.Text.Split(' ', '(')[0], t.Entry.FullTypeName, t.Entry.FullTypeName, t.Entry, m.Entry, assemblyPath))
            .Prepend(new AssemblySearchEntry(t.Entry.Name, t.Entry.Namespace, t.Entry.FullTypeName, t.Entry, null, assemblyPath)))
            .ToList();

    public DefinitionTokens? ResolveDefinitionTokens(IDefinitionResult definition) => null;

    // The callers the fixture has: Gamma.Call calls Run, DeltaUser.Use calls Go (through the base).
    public IReadOnlyList<AssemblySearchEntry> Analyze(
        AnalysisKind kind, string targetAssemblyPath, int targetToken, IReadOnlyList<string> assemblyPaths,
        CancellationToken cancellationToken, bool includeVirtualCalls)
    {
        if (kind != AnalysisKind.UsedBy) return [];
        var callerToken = targetToken switch
        {
            0x06000001 => 0x06000002,
            0x06000004 when includeVirtualCalls => 0x06000005,
            _ => 0,
        };
        return callerToken == 0 ? [] : BuildSearchIndex(targetAssemblyPath).Where(e => e.Member?.MetadataToken == callerToken).ToList();
    }
}
