using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.Output;
using ICSharpCode.Decompiler.TypeSystem;
using Invoklet.Contracts.IntelliSense;

namespace Invoklet.Languages.DotnetCommon;

/// <summary>
/// Decompiled source for a "Go to Definition" (F12) result that fell outside the user's own script —
/// see <see cref="CSharpDecompilerService"/>. <see cref="SyntaxTree"/> is the location-tracked AST
/// that produced <see cref="Text"/> (null only for the "type not found" fallback text, which has no
/// real tree behind it) — kept around so a caller showing this in a window can feed a later caret
/// position back into <see cref="CSharpDecompilerService.ResolveReferenceAtPosition"/> for recursive
/// F12 navigation, without re-decompiling from scratch just to get a fresh tree.
/// </summary>
public sealed record DecompiledMemberResult(string Text, int Line, string Title, SyntaxTree? SyntaxTree = null);

/// <summary>
/// What <see cref="CSharpDecompilerService.ResolveReferenceAtPosition"/> found under the caret — an
/// <see cref="IDefinitionResult"/> ready for <see cref="CSharpDecompilerService.Decompile"/>, plus the
/// target's <see cref="MetadataToken"/> (the type's, or the member definition's) within
/// <see cref="AssemblyPath"/>, which lets the assembly explorer find the matching tree node.
/// </summary>
public sealed record DecompiledReference(
    string AssemblyPath, string ContainingTypeFullName, string? MemberName,
    IReadOnlyList<string> ParameterTypeNames, string DisplayName, int MetadataToken,
    int ContainingTypeMetadataToken) : IDefinitionResult
{
    public bool IsInSource => false;
    public int Offset => 0;
}

/// <summary>The tokens of a definition's type and member (<see cref="CSharpDecompilerService.ResolveDefinitionTokens"/>).</summary>
public sealed record DefinitionTokens(int TypeToken, int? MemberToken);

/// <summary>A 1-based text span (end exclusive) in decompiled code: a navigable identifier.</summary>
public sealed record ReferenceSpan(int StartLine, int StartColumn, int EndLine, int EndColumn);

/// <summary>What an Assembly Explorer node is — drives its icon (ILSpy-style tree).</summary>
public enum AssemblyNodeKind
{
    Namespace,
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
    Field,
    Property,
    Event,
    Constructor,
    Method,
    BaseTypes,
    DerivedTypes,
    Assembly,
    References,
    Reference,
    Resources,
    Resource,
}

/// <summary>C# accessibility of a tree node — drives ILSpy's access overlay (lock, key, house).</summary>
public enum AssemblyAccess
{
    Public,
    Protected,
    ProtectedInternal,
    Internal,
    PrivateProtected,
    Private,
}

/// <summary>
/// A type in the assembly explorer (<c>AssemblyExplorerWindow</c>): a top-level or nested
/// type, or a base/derived type entry. <see cref="FullTypeName"/> (reflection name) feeds
/// <see cref="CSharpDecompilerService.DecompileType"/>; <see cref="AssemblyPath"/> is the assembly that
/// defines it when that differs from the browsed one (a base type from the BCL) — null means the browsed
/// assembly, or that the type couldn't be resolved at all (<see cref="IsResolved"/>).
/// </summary>
public sealed record AssemblyTypeEntry(
    string Namespace, string Name, string FullTypeName,
    AssemblyNodeKind Kind = AssemblyNodeKind.Class, AssemblyAccess Access = AssemblyAccess.Public,
    string? DisplayName = null, string? AssemblyPath = null, bool IsResolved = true,
    bool IsStatic = false, bool IsAbstract = false, int MetadataToken = 0)
{
    /// <summary>ILSpy-style tree text: generic types with their parameters (<c>Dictionary&lt;TKey, TValue&gt;</c>).</summary>
    public string Text => DisplayName ?? Name;

    /// <summary>Visible outside its assembly (ILSpy greys everything else).</summary>
    public bool IsPublic => CSharpDecompilerService.IsVisible(Access);
}

/// <summary>A field/property/event/constructor/method of a type, as the explorer lists it.</summary>
/// <param name="Text">ILSpy-style: <c>name : type</c>, <c>Name(params) : return</c>, <c>TypeName(params)</c>.</param>
/// <param name="MetadataToken">Feeds <see cref="CSharpDecompilerService.DecompileMember"/>.</param>
/// <param name="IsAbstract">Abstract in a class (interface members don't count — they all are).</param>
/// <param name="IsOverridable">Virtual, abstract or a non-sealed override in a class (Analyze: Overridden By).</param>
/// <param name="IsInterfaceMember">Declared by an interface (Analyze: Implemented By).</param>
public sealed record AssemblyMemberEntry(
    AssemblyNodeKind Kind, string Text, AssemblyAccess Access, int MetadataToken,
    bool IsStatic = false, bool IsAbstract = false, bool IsOverridable = false, bool IsInterfaceMember = false)
{
    /// <summary>Visible outside its assembly (ILSpy greys everything else).</summary>
    public bool IsPublic => CSharpDecompilerService.IsVisible(Access);
}

/// <summary>
/// One hit of the explorer's search (<see cref="CSharpDecompilerService.BuildSearchIndex"/>): a type
/// (<see cref="Member"/> null) or a member of <see cref="Type"/>. <see cref="Name"/> is what's matched
/// (a constructor matches by its type's name); <see cref="Location"/> is where it's declared — the
/// namespace of a top-level type, the declaring type otherwise. <see cref="TypeFullName"/> (the type's,
/// or the declaring type's, reflection name) is what the tree reveals.
/// </summary>
public sealed record AssemblySearchEntry(
    string Name, string Location, string TypeFullName, AssemblyTypeEntry Type, AssemblyMemberEntry? Member,
    string AssemblyPath)
{
    public AssemblyNodeKind Kind => Member?.Kind ?? Type.Kind;
    public string Text => Member?.Text ?? Type.Text;
    internal string QualifiedName => Location.Length > 0 ? Location + "." + Name : Name;

    /// <summary><see cref="QualifiedName"/> without type argument lists: "Tree.Box&lt;T&gt;.Put" → "Tree.Box.Put".</summary>
    internal string PlainQualifiedName => WithoutTypeArguments(QualifiedName);

    private static string WithoutTypeArguments(string name)
    {
        if (!name.Contains('<')) return name;
        var plain = new System.Text.StringBuilder(name.Length);
        var depth = 0;
        foreach (var c in name)
        {
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (depth == 0) plain.Append(c);
        }
        return plain.ToString();
    }
}

/// <summary>
/// An assembly row of the explorer (<see cref="CSharpDecompilerService.DescribeAssembly"/>).
/// <see cref="Token"/> is the AssemblyDefinition token: <see cref="CSharpDecompilerService.DecompileEntity"/>
/// shows the assembly's attributes for it, <c>IlDisassembler.DisassembleEntity</c> its manifest.
/// </summary>
public sealed record AssemblyDescription(
    string Name, string FullName, Version? Version, IReadOnlyList<AssemblyReferenceEntry> References,
    IReadOnlyList<AssemblyResourceEntry> Resources)
{
    public const int Token = 0x20000001;
}

/// <summary>A referenced assembly; <see cref="ResolvedPath"/> null when the file can't be found.</summary>
public sealed record AssemblyReferenceEntry(string Name, string FullName, string? ResolvedPath);

/// <summary>What a type node expands to, in ILSpy's order.</summary>
public sealed record AssemblyTypeContents(
    IReadOnlyList<AssemblyTypeEntry> BaseTypes,
    IReadOnlyList<AssemblyTypeEntry> DerivedTypes,
    IReadOnlyList<AssemblyTypeEntry> NestedTypes,
    IReadOnlyList<AssemblyMemberEntry> Members);

/// <summary>
/// Decompiles the type an <see cref="IDefinitionResult"/> points at ("Go to Definition"/
/// ILSpy integration) and locates the specific member's declaration line
/// within the decompiled text, so the caller can open a read-only tab already scrolled to the right
/// place — mirrors dnSpy/ILSpy's own F12 behavior. <see cref="ResolveReferenceAtPosition"/> is the
/// other half that makes this recursive: given a
/// caret position inside an ALREADY-decompiled tree, resolve what's referenced there into another
/// <see cref="IDefinitionResult"/> that can be fed straight back into <see cref="Decompile"/> — same
/// contract either way, whether the original request came from Roslyn (a symbol in the user's own
/// script) or from clicking around inside decompiled code that isn't Roslyn-backed at all.
/// <see cref="EnumerateTypes"/>/<see cref="DecompileType"/> are the third leg — browsing an arbitrary DLL the user
/// picked, not one reached by
/// following a reference from their own script.
/// </summary>
public static partial class CSharpDecompilerService
{
    public static DecompiledMemberResult Decompile(IDefinitionResult definition)
    {
        if (definition.IsInSource || definition.AssemblyPath is not { } assemblyPath
            || definition.ContainingTypeFullName is not { } typeName)
        {
            throw new ArgumentException(
                "Definition does not carry external-symbol info to decompile.", nameof(definition));
        }

        return WithDecompiler(assemblyPath, (decompiler, settings) =>
        {
            var fullTypeName = new FullTypeName(typeName);
            var typeDef = decompiler.TypeSystem.FindType(fullTypeName).GetDefinition();
            if (typeDef is null)
            {
                return new DecompiledMemberResult(
                    $"// Could not find type '{typeName}' in '{assemblyPath}'.", 1, definition.DisplayName);
            }

            var syntaxTree = decompiler.DecompileType(fullTypeName);
            AddHeader(syntaxTree, decompiler, typeDef);
            var text = RenderWithLocations(syntaxTree, settings);

            ISymbol? target = definition.MemberName is { } memberName
                ? FindMember(typeDef, memberName, definition.ParameterTypeNames)
                : typeDef;

            var line = FindDeclarationLine(syntaxTree, target) ?? 1;

            return new DecompiledMemberResult(text, line, definition.DisplayName, syntaxTree);
        });
    }

    /// <summary>
    /// Convenience wrapper around <see cref="Decompile"/> for callers that already have a bare
    /// assembly path + type name on hand (the assembly explorer's tree, not a Roslyn/decompiler
    /// symbol resolution) — builds the throwaway <see cref="IDefinitionResult"/> itself.
    /// </summary>
    /// <summary>
    /// The metadata tokens of what <paramref name="definition"/> points at — its containing type and,
    /// when there is one, the member (picked by <see cref="FindMember"/>, the same choice
    /// <see cref="Decompile"/> puts the caret on). Lets the assembly explorer find the tree rows of an
    /// F12 that started in the user's own script, which carries names but no tokens. Null when the
    /// definition has no assembly/type or the type isn't in that assembly.
    /// </summary>
    public static DefinitionTokens? ResolveDefinitionTokens(IDefinitionResult definition)
    {
        if (definition.AssemblyPath is not { } assemblyPath || definition.ContainingTypeFullName is not { } typeName)
            return null;

        return WithDecompiler(assemblyPath, (decompiler, _) =>
        {
            if (decompiler.TypeSystem.FindType(new FullTypeName(typeName)).GetDefinition() is not { } typeDef)
                return null;
            var member = definition.MemberName is { } memberName
                ? FindMember(typeDef, memberName, definition.ParameterTypeNames)
                : null;
            return new DefinitionTokens(
                MetadataTokens.GetToken(typeDef.MetadataToken),
                member is null ? null : MetadataTokens.GetToken(member.MetadataToken));
        });
    }

    public static DecompiledMemberResult DecompileType(string assemblyPath, string typeFullName, string? displayName = null)
        => Decompile(new TypeOnlyDefinitionResult(assemblyPath, typeFullName, displayName ?? typeFullName));

    /// <summary>
    /// Lists every top-level (non-nested) type in <paramref name="assemblyPath"/>, grouped by
    /// namespace by the caller (<c>AssemblyExplorerWindow</c>'s tree) — compiler-generated noise
    /// (<c>&lt;Module&gt;</c>, closure classes like <c>&lt;&gt;c__DisplayClass0_0</c>, ...) filtered
    /// out by name (all start with <c>&lt;</c>, which is not a legal identifier character in either
    /// C# or VB — nothing a real declared type could ever collide with). Nested types (nested
    /// classes/records a real author declared, not just compiler-generated ones) are left for the
    /// user to reach by decompiling their declaring type and reading the source, rather than listed
    /// as their own tree nodes — keeps the initial tree to "one screen per assembly" for anything
    /// short of a huge one, matching the TODO's own scope ("tree of all types/namespaces").
    /// </summary>
    public static IReadOnlyList<AssemblyTypeEntry> EnumerateTypes(string assemblyPath)
    {
        return WithDecompiler(assemblyPath, (decompiler, _) => decompiler.TypeSystem.MainModule.TypeDefinitions
            .Where(t => t.DeclaringTypeDefinition is null && !t.Name.StartsWith('<'))
            .Select(t => ToEntry(t, browsedAssembly: null))
            .OrderBy(e => e.Namespace, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList());
    }

    /// <summary>
    /// What a type node of the ILSpy-style tree expands to: base types (direct base class + interfaces),
    /// derived types within <paramref name="assemblyPath"/>, nested types and members — fields,
    /// properties, events, constructors, methods, alphabetical within each group. Compiler-generated
    /// members (names starting with <c>&lt;</c>) and property/event accessors are left out, like ILSpy.
    /// </summary>
    public static AssemblyTypeContents GetTypeContents(string assemblyPath, string typeFullName)
    {
        return WithDecompiler(assemblyPath, (decompiler, _) =>
        {
            var typeDef = decompiler.TypeSystem.FindType(new FullTypeName(typeFullName)).GetDefinition();
            if (typeDef is null) return new AssemblyTypeContents([], [], [], []);

            var baseTypes = typeDef.DirectBaseTypes
                .Where(t => t.Kind != TypeKind.Unknown || t.Name.Length > 0)
                .Select(t => t.GetDefinition() is { } def
                    ? ToEntry(def, assemblyPath) with { DisplayName = TypeText(t) }
                    : new AssemblyTypeEntry(t.Namespace, t.Name, t.ReflectionName, DisplayName: TypeText(t), IsResolved: false))
                .ToList();

            var derivedTypes = decompiler.TypeSystem.MainModule.TypeDefinitions
                .Where(t => !t.Name.StartsWith('<')
                            && t.DirectBaseTypes.Any(b => b.GetDefinition() is { } def && def.Equals(typeDef)))
                .Select(t => ToEntry(t, assemblyPath))
                .OrderBy(e => e.Text, StringComparer.Ordinal)
                .ToList();

            var nestedTypes = typeDef.NestedTypes
                .Where(t => !t.Name.StartsWith('<'))
                .Select(t => ToEntry(t, assemblyPath))
                .OrderBy(e => e.Text, StringComparer.Ordinal)
                .ToList();

            var members = ListedMembers(typeDef)
                .Select(m => ToMemberEntry(typeDef, m))
                .OrderBy(m => m.Kind)
                .ThenBy(m => m.Text, StringComparer.Ordinal)
                .ToList();

            return new AssemblyTypeContents(baseTypes, derivedTypes, nestedTypes, members);
        });
    }

    /// <summary>
    /// Decompiles ONE member (field, property, event, constructor, method) on its own, the way ILSpy
    /// shows a selected member — not its whole declaring type — under the assembly/declaring-type header.
    /// </summary>
    public static DecompiledMemberResult DecompileMember(string assemblyPath, int metadataToken, string title)
        => DecompileEntity(assemblyPath, metadataToken, title);

    /// <summary>
    /// The assembly explorer's C# view of one entity, by metadata token: a type definition shows the
    /// whole type, anything else just that member (<see cref="DecompileMember"/>).
    /// <paramref name="languageVersion"/> is the C# the decompiler may use (ILSpy's version box: older
    /// versions spell out what newer syntax would shorten). <paramref name="focusToken"/>, when set,
    /// is the member within a type page to put the caret on (F12 to a member the tree doesn't list).
    /// </summary>
    public static DecompiledMemberResult DecompileEntity(
        string assemblyPath, int metadataToken, string title,
        LanguageVersion? languageVersion = null, int focusToken = 0)
    {
        return WithDecompiler(assemblyPath, languageVersion, (decompiler, settings) =>
        {
            var handle = MetadataTokens.EntityHandle(metadataToken);
            if (handle.Kind == HandleKind.AssemblyDefinition)
            {
                // the assembly row: its assembly- and module-level attributes, under the header
                var attributes = decompiler.DecompileModuleAndAssemblyAttributes();
                AddHeader(attributes, decompiler, declaringType: null);
                var attributesText = RenderWithLocations(attributes, settings);
                return new DecompiledMemberResult(
                    attributesText.Length > 0 ? attributesText : $"// {decompiler.TypeSystem.MainModule.FullAssemblyName}\n",
                    1, title, attributes);
            }

            var entity = decompiler.TypeSystem.MainModule.ResolveEntity(handle);
            SyntaxTree syntaxTree;
            if (entity is ITypeDefinition typeDef)
            {
                syntaxTree = decompiler.DecompileType(typeDef.FullTypeName);
                AddHeader(syntaxTree, decompiler, typeDef);
            }
            else
            {
                syntaxTree = decompiler.Decompile(handle);
                AddHeader(syntaxTree, decompiler, entity?.DeclaringTypeDefinition);
            }

            var text = RenderWithLocations(syntaxTree, settings);
            var focus = focusToken != 0
                ? decompiler.TypeSystem.MainModule.ResolveEntity(MetadataTokens.EntityHandle(focusToken))
                : entity;
            var line = FindDeclarationLine(syntaxTree, focus) ?? 1;
            return new DecompiledMemberResult(text, line, title, syntaxTree);
        });
    }

    // ILSpy's header: the assembly's full name, then (for a member) its declaring type — as comments
    // inside the tree itself, so rendered locations (F12, declaration lines) stay consistent.
    private static void AddHeader(SyntaxTree syntaxTree, CSharpDecompiler decompiler, ITypeDefinition? declaringType)
    {
        if (syntaxTree.FirstChild is not { } first) return;
        first.AddLeadingTrivia(new Comment(" " + decompiler.TypeSystem.MainModule.FullAssemblyName));
        if (declaringType is not null)
            first.AddLeadingTrivia(new Comment(" " + FullTypeText(declaringType)));
    }

    // The members the tree lists: compiler-generated ones (names starting with '<'), property/event
    // accessors and a field-like event's backing field (it shares the event's name) are left out.
    private static IEnumerable<IMember> ListedMembers(ITypeDefinition type) => type.Members
        .Where(m => !m.Name.StartsWith('<') && m is not IMethod { IsAccessor: true }
                    && !(m is IField && m.HasAttribute(KnownAttribute.CompilerGenerated)));

    private static AssemblyMemberEntry ToMemberEntry(ITypeDefinition declaringType, IMember member) => new(
        MemberKind(member), MemberText(member), ToAccess(member.Accessibility),
        MetadataTokens.GetToken(member.MetadataToken), member.IsStatic,
        member.IsAbstract && declaringType.Kind != TypeKind.Interface,
        // a sealed class (or a struct) seals its overrides too, whatever the member itself says
        IsOverridable: member.IsOverridable && declaringType.Kind != TypeKind.Interface && !declaringType.IsSealed,
        IsInterfaceMember: declaringType.Kind == TypeKind.Interface);

    // Neither the type nor any type it's nested in is compiler-generated (closures, state machines).
    private static bool IsListed(ITypeDefinition type)
    {
        for (var t = type; t is not null; t = t.DeclaringTypeDefinition)
        {
            if (t.Name.StartsWith('<')) return false;
        }
        return true;
    }

    /// <summary>
    /// Everything the explorer's search can find in <paramref name="assemblyPath"/>: every listed type
    /// (nested ones too) and every member the tree would list, built once per assembly and then
    /// filtered in memory by <see cref="Search"/> on each keystroke.
    /// </summary>
    public static IReadOnlyList<AssemblySearchEntry> BuildSearchIndex(string assemblyPath)
    {
        return WithDecompiler(assemblyPath, (decompiler, _) =>
        {
            var entries = new List<AssemblySearchEntry>();
            foreach (var type in decompiler.TypeSystem.MainModule.TypeDefinitions.Where(IsListed))
            {
                var typeEntry = ToEntry(type, browsedAssembly: null);
                var typeName = type.FullTypeName.ReflectionName;
                var location = type.DeclaringTypeDefinition is { } outer ? FullTypeText(outer) : type.Namespace;
                entries.Add(new AssemblySearchEntry(type.Name, location, typeName, typeEntry, Member: null, assemblyPath));

                var memberLocation = FullTypeText(type);
                foreach (var member in ListedMembers(type))
                {
                    var name = member is IMethod { IsConstructor: true } ? type.Name : member.Name;
                    entries.Add(new AssemblySearchEntry(
                        name, memberLocation, typeName, typeEntry, ToMemberEntry(type, member), assemblyPath));
                }
            }
            return entries;
        });
    }

    /// <summary>
    /// ILSpy-style name search over a <see cref="BuildSearchIndex"/> result, case-insensitive: exact
    /// name matches first, then names starting with <paramref name="query"/>, then names containing it,
    /// alphabetical within each. A query with a '.' matches the qualified name instead
    /// (<c>Shape.Echo</c>, <c>Tree.Shape</c>). At most <paramref name="limit"/> results.
    /// </summary>
    public static IReadOnlyList<AssemblySearchEntry> Search(
        IReadOnlyList<AssemblySearchEntry> index, string query, int limit = 200)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        var qualified = query.Contains('.');

        return index
            // a dotted query matches the qualified name with or without type arguments
            // ("Box.Put" and "Box<T>.Put" both find Tree.Box<T>.Put)
            .Select(e => (Entry: e, Rank: qualified ? BestRank(e.QualifiedName, e.PlainQualifiedName) : Rank(e.Name)))
            .Where(r => r.Rank >= 0)
            .OrderBy(r => r.Rank)
            .ThenBy(r => r.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Entry.Location, StringComparer.Ordinal)
            .Take(limit)
            .Select(r => r.Entry)
            .ToList();

        int BestRank(string name, string plainName)
        {
            var (full, plain) = (Rank(name), Rank(plainName));
            return full < 0 ? plain : plain < 0 ? full : Math.Min(full, plain);
        }

        int Rank(string name) =>
            name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0
            : name.EndsWith("." + query, StringComparison.OrdinalIgnoreCase) ? 0
            : name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1
            : name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2
            : -1;
    }

    private static AssemblyTypeEntry ToEntry(ITypeDefinition type, string? browsedAssembly)
    {
        var definingAssembly = type.ParentModule?.MetadataFile?.FileName;
        return new AssemblyTypeEntry(
            type.Namespace, type.Name, type.FullTypeName.ReflectionName,
            type.Kind switch
            {
                TypeKind.Interface => AssemblyNodeKind.Interface,
                TypeKind.Struct => AssemblyNodeKind.Struct,
                TypeKind.Enum => AssemblyNodeKind.Enum,
                TypeKind.Delegate => AssemblyNodeKind.Delegate,
                _ => AssemblyNodeKind.Class,
            },
            ToAccess(type.Accessibility),
            TypeText(type),
            browsedAssembly is not null && !string.Equals(definingAssembly, browsedAssembly, StringComparison.OrdinalIgnoreCase)
                ? definingAssembly
                : null,
            IsStatic: type.IsStatic,
            // a static class is abstract + sealed in metadata; only a real abstract class counts
            IsAbstract: type.Kind == TypeKind.Class && type.IsAbstract && !type.IsStatic,
            MetadataToken: MetadataTokens.GetToken(type.MetadataToken));
    }

    private static AssemblyAccess ToAccess(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => AssemblyAccess.Public,
        Accessibility.Protected => AssemblyAccess.Protected,
        Accessibility.ProtectedOrInternal => AssemblyAccess.ProtectedInternal,
        Accessibility.Internal => AssemblyAccess.Internal,
        Accessibility.ProtectedAndInternal => AssemblyAccess.PrivateProtected,
        _ => AssemblyAccess.Private,
    };

    // ILSpy greys non-public API: private and internal (incl. private protected) look disabled.
    internal static bool IsVisible(AssemblyAccess access) =>
        access is AssemblyAccess.Public or AssemblyAccess.Protected or AssemblyAccess.ProtectedInternal;

    private static AssemblyNodeKind MemberKind(IMember member) => member switch
    {
        IField => AssemblyNodeKind.Field,
        IProperty => AssemblyNodeKind.Property,
        IEvent => AssemblyNodeKind.Event,
        IMethod { IsConstructor: true } => AssemblyNodeKind.Constructor,
        _ => AssemblyNodeKind.Method,
    };

    // ILSpy's tree text: "name : type", "Name<T>(params) : return", "TypeName(params)", "this[params] : type".
    private static string MemberText(IMember member)
    {
        string Parameters(IReadOnlyList<IParameter> parameters) => string.Join(", ", parameters.Select(p =>
            (p.ReferenceKind switch
            {
                ReferenceKind.Ref => "ref ",
                ReferenceKind.Out => "out ",
                ReferenceKind.In => "in ",
                _ => "",
            }) + TypeText(p.Type is ByReferenceType byRef ? byRef.ElementType : p.Type)));

        // an explicit interface implementation is named "IGreeter.Greet" like ILSpy, not by its metadata
        // name ("Tree.IGreeter.Greet"): the interface as written (short, with type arguments) + its member
        var implemented = member.IsExplicitInterfaceImplementation
            ? member.ExplicitlyImplementedInterfaceMembers.FirstOrDefault()
            : null;
        var prefix = implemented is null ? "" : TypeText(implemented.DeclaringType) + ".";
        var name = implemented?.Name ?? member.Name;

        switch (member)
        {
            case IProperty { IsIndexer: true } indexer:
                return $"{prefix}this[{Parameters(indexer.Parameters)}] : {TypeText(indexer.ReturnType)}";
            case IMethod { IsConstructor: true } ctor:
                return $"{ctor.DeclaringTypeDefinition?.Name ?? ctor.Name}({Parameters(ctor.Parameters)})";
            case IMethod method:
                var typeParameters = method.TypeParameters.Count > 0
                    ? "<" + string.Join(", ", method.TypeParameters.Select(t => t.Name)) + ">"
                    : "";
                return $"{prefix}{name}{typeParameters}({Parameters(method.Parameters)}) : {TypeText(method.ReturnType)}";
            default:
                return $"{prefix}{name} : {TypeText(member.ReturnType)}";
        }
    }

    // Short C# type names with keywords and type arguments: "int", "IEqualityComparer<TKey>".
    private static readonly CSharpAmbience ShortTypeAmbience = new()
    {
        ConversionFlags = ConversionFlags.ShowTypeParameterList | ConversionFlags.UseNullableSpecifierForValueTypes,
    };

    private static string TypeText(IType type) => ShortTypeAmbience.ConvertType(type);

    // "System.Collections.Generic.Dictionary<TKey, TValue>", nested types joined by '.'.
    private static string FullTypeText(ITypeDefinition type)
        => type.DeclaringTypeDefinition is { } declaring
            ? FullTypeText(declaring) + "." + TypeText(type)
            : (type.Namespace.Length > 0 ? type.Namespace + "." : "") + TypeText(type);

    // Owns the PEFile for exactly the duration of `body` (CSharpDecompiler's (MetadataFile, ...)
    // overload below instead of the convenience CSharpDecompiler(string, ...) constructor, which
    // opens an internal PEFile we'd have no handle to dispose) — PEFile.Dispose() releases the
    // FileStream it opens on assemblyPath; an undisposed one outlives the call on any OS that
    // enforces exclusive file locks (Windows; POSIX allows unlinking/rebuilding an open file, so this
    // never surfaced there — live Windows CI report, 2026-07-19, on the same leak in
    // IlDisassembler.Disassemble). The default single-arg CSharpDecompiler(path, settings) ctor
    // builds its own resolver, but that resolver's search-path heuristics (well-known install
    // locations, DOTNET_ROOT, etc.) don't find every real-world .NET install — e.g. dotnet installed
    // via linuxbrew at a nonstandard prefix. Adding THIS process's own runtime directory explicitly
    // (the compiled script runs against the very same installed runtime) is a robust fallback either way.
    private static T WithDecompiler<T>(string assemblyPath, Func<CSharpDecompiler, DecompilerSettings, T> body)
        => WithDecompiler(assemblyPath, languageVersion: null, body);

    // languageVersion null: DecompilerSettings' defaults (every feature on), as before the version box.
    private static T WithDecompiler<T>(
        string assemblyPath, LanguageVersion? languageVersion, Func<CSharpDecompiler, DecompilerSettings, T> body)
    {
        // `using var peFile = new PEFile(assemblyPath)` alone only disposes peFile once construction
        // SUCCEEDS — that convenience constructor opens a FileStream inline as part of a chained
        // constructor call, and if MetadataFile's base constructor then throws while parsing headers
        // (a corrupt/non-assembly file — exactly what AssemblyExplorerWindow's own "not a real
        // assembly" error-dialog test feeds it), the stream is never assigned anywhere `using` could
        // reach, so it leaks regardless of the `using` on peFile itself — a second live Windows CI
        // report, 2026-07-20, same file-lock symptom as the leak this comment already used to document
        // here, just triggered by an INVALID file failing inside the constructor instead of a valid
        // one succeeding and leaking only after the method returned. Opening the stream ourselves
        // first gives it its own `using`, independent of whether the PEFile constructor consuming it
        // succeeds or throws.
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read);
        using var peFile = new PEFile(assemblyPath, stream);
        var settings = languageVersion is { } version ? new DecompilerSettings(version) : new DecompilerSettings();
        var decompiler = new CSharpDecompiler(peFile, CreateResolver(assemblyPath, peFile), settings);
        return body(decompiler, settings);
    }

    private static UniversalAssemblyResolver CreateResolver(string assemblyPath, PEFile peFile)
    {
        var resolver = new UniversalAssemblyResolver(
            assemblyPath, throwOnError: false, peFile.DetectTargetFrameworkId());
        resolver.AddSearchDirectory(RuntimeEnvironment.GetRuntimeDirectory());
        return resolver;
    }

    /// <summary>
    /// The assembly explorer's assembly row: its name and full name, and its References node — every
    /// referenced assembly with the file it resolves to (null when it can't be found), resolved the same
    /// way decompilation resolves them, so a reference that opens here is the one F12 would land in.
    /// </summary>
    public static AssemblyDescription DescribeAssembly(string assemblyPath)
    {
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read);
        using var peFile = new PEFile(assemblyPath, stream);
        var resolver = CreateResolver(assemblyPath, peFile);
        var references = peFile.AssemblyReferences
            .Select(r => new AssemblyReferenceEntry(r.Name, r.FullName, resolver.FindAssemblyFile(r)))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var version = peFile.Metadata.IsAssembly ? peFile.Metadata.GetAssemblyDefinition().Version : null;
        return new AssemblyDescription(peFile.Name, peFile.FullName, version, references, ResourcesOf(peFile));
    }

    /// <summary>
    /// Resolves whatever <paramref name="tree"/> — a <see cref="SyntaxTree"/> a previous
    /// <see cref="Decompile"/> call returned — has at (<paramref name="line"/>, <paramref name="column"/>)
    /// into an <see cref="IDefinitionResult"/> pointing at ITS declaration, ready to feed straight back
    /// into <see cref="Decompile"/>. Null if there's nothing resolvable there (whitespace, a keyword, a
    /// local variable — anything without an <see cref="ICSharpCode.Decompiler.TypeSystem.IEntity"/>
    /// behind it) or the resolved entity's own assembly couldn't be located on disk (an unresolved
    /// reference — <see cref="UniversalAssemblyResolver"/> didn't find it under the search directories
    /// <see cref="Decompile"/> configures).
    /// </summary>
    public static DecompiledReference? ResolveReferenceAtPosition(SyntaxTree tree, int line, int column)
    {
        var node = FindInnermostNodeAt(tree, line, column);

        // The innermost node at a position is typically a bare Identifier, which never carries its
        // own resolved symbol — the annotation lives on an ancestor (MemberReferenceExpression,
        // InvocationExpression, the declaration itself, ...). Walk up until one has it.
        while (node is not null && node.GetSymbol() is null)
            node = node.Parent;

        ITypeDefinition? containingType;
        string? memberName;
        IReadOnlyList<string> parameterTypeNames;
        string displayName;
        int metadataToken;

        switch (node?.GetSymbol())
        {
            case ITypeDefinition typeDef:
                containingType = typeDef;
                memberName = null;
                parameterTypeNames = [];
                displayName = typeDef.FullTypeName.ReflectionName;
                metadataToken = MetadataTokens.GetToken(typeDef.MetadataToken);
                break;
            case IMember member:
                containingType = member.DeclaringTypeDefinition;
                memberName = member.Name;
                parameterTypeNames = member is IParameterizedMember { Parameters: var parameters }
                    ? parameters.Select(p => p.Type.Name).ToList()
                    : [];
                displayName = member.FullName;
                // the definition's token, not a generic specialization's (List<int>.Add → List<T>.Add)
                metadataToken = MetadataTokens.GetToken(member.MemberDefinition.MetadataToken);
                break;
            default:
                return null; // e.g. a local variable, or genuinely nothing resolvable at this position
        }

        if (containingType is null) return null;

        var targetAssemblyPath = containingType.ParentModule?.MetadataFile?.FileName;
        if (targetAssemblyPath is null) return null; // e.g. an unresolved reference assembly

        return new DecompiledReference(
            targetAssemblyPath, containingType.FullTypeName.ReflectionName, memberName, parameterTypeNames, displayName,
            metadataToken, MetadataTokens.GetToken(containingType.MetadataToken));
    }

    /// <summary>
    /// The identifier at (<paramref name="line"/>, <paramref name="column"/>) when F12 there would go
    /// somewhere (<see cref="ResolveReferenceAtPosition"/> resolves it) — the span the explorer
    /// underlines while Ctrl is held over it. Null over whitespace, keywords, locals, or anything that
    /// isn't a bare identifier (so a whole expression is never underlined).
    /// </summary>
    public static ReferenceSpan? FindReferenceSpanAtPosition(SyntaxTree tree, int line, int column)
    {
        if (FindInnermostNodeAt(tree, line, column) is not Identifier identifier) return null;
        if (ResolveReferenceAtPosition(tree, line, column) is null) return null;
        return new ReferenceSpan(
            identifier.StartLocation.Line, identifier.StartLocation.Column,
            identifier.EndLocation.Line, identifier.EndLocation.Column);
    }

    // The innermost node containing (line, column) — Descendants is pre-order (parent before its own
    // children), so the LAST match in that sequence is the most deeply nested one, i.e. the smallest
    // span still containing the position.
    private static AstNode? FindInnermostNodeAt(SyntaxTree tree, int line, int column)
    {
        AstNode? best = null;
        foreach (var node in tree.Descendants)
        {
            if (node.Contains(line, column)) best = node;
        }
        return best;
    }

    private static string RenderWithLocations(SyntaxTree syntaxTree, DecompilerSettings settings)
    {
        var stringWriter = new StringWriter();
        var tokenWriter = TokenWriter.CreateWriterThatSetsLocationsInAST(stringWriter);
        var visitor = new CSharpOutputVisitor(tokenWriter, settings.CSharpFormattingOptions);
        syntaxTree.AcceptVisitor(visitor);
        return stringWriter.ToString();
    }

    private static int? FindDeclarationLine(SyntaxTree syntaxTree, ISymbol? target)
    {
        if (target is null) return null;
        foreach (var node in syntaxTree.Descendants.OfType<EntityDeclaration>())
        {
            if (Equals(node.GetSymbol(), target)) return node.StartLocation.Line;
        }
        return null;
    }

    // Roslyn's ISymbol and the decompiler's own ISymbol are unrelated type systems — IDefinitionResult
    // only carries a name + ordered parameter type names (see CSharpIntelliSenseService), which is
    // enough to disambiguate the common overload case but not a full signature match. Falls back to
    // "first member with this name" (single-Member fields/properties/events, or an unmatched method
    // overload) rather than returning nothing — an imprecise line beats no navigation at all.
    // Two Roslyn/metadata naming gaps are bridged here, both of which used to land on the type instead:
    // Roslyn names an indexer "this[]" (metadata: "Item"), and an accessor symbol (get_X/add_X) stands
    // for its property/event — which is what the explorer's tree lists and the caret should be on.
    private static IMember? FindMember(ITypeDefinition typeDef, string memberName, IReadOnlyList<string> parameterTypeNames)
    {
        var candidates = typeDef.Members
            .Where(m => memberName == "this[]" ? m is IProperty { IsIndexer: true } : m.Name == memberName)
            .ToList();
        if (candidates.Count == 0)
        {
            // an accessor's name: typeDef.Members doesn't list accessors, their property/event does
            candidates = typeDef.Properties
                .Where(p => p.Getter?.Name == memberName || p.Setter?.Name == memberName)
                .Cast<IMember>()
                .Concat(typeDef.Events.Where(e => e.AddAccessor?.Name == memberName
                                                  || e.RemoveAccessor?.Name == memberName
                                                  || e.InvokeAccessor?.Name == memberName))
                .ToList();
        }
        var found = candidates.Count <= 1 ? candidates.FirstOrDefault() : MatchParameters() ?? candidates[0];
        return found is IMethod { AccessorOwner: IMember owner } ? owner : found;

        IMember? MatchParameters()
        {
            foreach (var candidate in candidates)
            {
                if (candidate is not IParameterizedMember { Parameters: var parameters }) continue;
                if (parameters.Count != parameterTypeNames.Count) continue;
                if (parameters.Select(p => p.Type.Name).SequenceEqual(parameterTypeNames)) return candidate;
            }
            return null;
        }
    }

    private sealed record TypeOnlyDefinitionResult(
        string AssemblyPath, string ContainingTypeFullName, string DisplayName) : IDefinitionResult
    {
        public bool IsInSource => false;
        public int Offset => 0;
        public string? MemberName => null;
        public IReadOnlyList<string> ParameterTypeNames => [];
    }
}
