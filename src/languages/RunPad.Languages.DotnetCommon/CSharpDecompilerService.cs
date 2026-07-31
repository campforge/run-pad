using System.Runtime.InteropServices;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using RunPad.Contracts.IntelliSense;

namespace RunPad.Languages.DotnetCommon;

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
/// One top-level (non-nested) type found by <see cref="CSharpDecompilerService.EnumerateTypes"/> —
/// TODO.md #62's remaining half, the ILSpy-style assembly explorer (<c>AssemblyExplorerWindow</c>).
/// <see cref="FullTypeName"/> is ready to feed straight into <see cref="CSharpDecompilerService.DecompileType"/>.
/// </summary>
public sealed record AssemblyTypeEntry(string Namespace, string Name, string FullTypeName);

/// <summary>
/// Decompiles the type an <see cref="IDefinitionResult"/> points at (TODO.md #62, "Go to Definition"/
/// ILSpy integration, user request 2026-07-19) and locates the specific member's declaration line
/// within the decompiled text, so the caller can open a read-only tab already scrolled to the right
/// place — mirrors dnSpy/ILSpy's own F12 behavior. <see cref="ResolveReferenceAtPosition"/> is the
/// other half that makes this recursive (TODO.md #62, user request 2026-07-19 follow-up): given a
/// caret position inside an ALREADY-decompiled tree, resolve what's referenced there into another
/// <see cref="IDefinitionResult"/> that can be fed straight back into <see cref="Decompile"/> — same
/// contract either way, whether the original request came from Roslyn (a symbol in the user's own
/// script) or from clicking around inside decompiled code that isn't Roslyn-backed at all.
/// <see cref="EnumerateTypes"/>/<see cref="DecompileType"/> are the third leg (TODO.md #62, user
/// request 2026-07-19 follow-up) — browsing an arbitrary DLL the user picked, not one reached by
/// following a reference from their own script.
/// </summary>
public static class CSharpDecompilerService
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
            .Select(t => new AssemblyTypeEntry(t.Namespace, t.Name, t.FullTypeName.ReflectionName))
            .OrderBy(e => e.Namespace, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList());
    }

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
        var resolver = new UniversalAssemblyResolver(
            assemblyPath, throwOnError: false, peFile.DetectTargetFrameworkId());
        resolver.AddSearchDirectory(RuntimeEnvironment.GetRuntimeDirectory());
        var settings = new DecompilerSettings();
        var decompiler = new CSharpDecompiler(peFile, resolver, settings);
        return body(decompiler, settings);
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
    public static IDefinitionResult? ResolveReferenceAtPosition(SyntaxTree tree, int line, int column)
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

        switch (node?.GetSymbol())
        {
            case ITypeDefinition typeDef:
                containingType = typeDef;
                memberName = null;
                parameterTypeNames = [];
                displayName = typeDef.FullTypeName.ReflectionName;
                break;
            case IMember member:
                containingType = member.DeclaringTypeDefinition;
                memberName = member.Name;
                parameterTypeNames = member is IParameterizedMember { Parameters: var parameters }
                    ? parameters.Select(p => p.Type.Name).ToList()
                    : [];
                displayName = member.FullName;
                break;
            default:
                return null; // e.g. a local variable, or genuinely nothing resolvable at this position
        }

        if (containingType is null) return null;

        var targetAssemblyPath = containingType.ParentModule?.MetadataFile?.FileName;
        if (targetAssemblyPath is null) return null; // e.g. an unresolved reference assembly

        return new RecursiveDefinitionResult(
            targetAssemblyPath, containingType.FullTypeName.ReflectionName, memberName, parameterTypeNames, displayName);
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
    private static IMember? FindMember(ITypeDefinition typeDef, string memberName, IReadOnlyList<string> parameterTypeNames)
    {
        var candidates = typeDef.Members.Where(m => m.Name == memberName).ToList();
        if (candidates.Count <= 1) return candidates.FirstOrDefault();

        foreach (var candidate in candidates)
        {
            if (candidate is not IParameterizedMember { Parameters: var parameters }) continue;
            if (parameters.Count != parameterTypeNames.Count) continue;
            if (parameters.Select(p => p.Type.Name).SequenceEqual(parameterTypeNames)) return candidate;
        }

        return candidates[0];
    }

    private sealed record RecursiveDefinitionResult(
        string AssemblyPath, string ContainingTypeFullName, string? MemberName,
        IReadOnlyList<string> ParameterTypeNames, string DisplayName) : IDefinitionResult
    {
        public bool IsInSource => false;
        public int Offset => 0;
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
