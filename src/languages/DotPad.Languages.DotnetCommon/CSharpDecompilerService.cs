using System.Runtime.InteropServices;
using DotPad.Contracts.IntelliSense;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace DotPad.Languages.DotnetCommon;

/// <summary>
/// Decompiled source for a "Go to Definition" (F12) result that fell outside the user's own script —
/// see <see cref="CSharpDecompilerService"/>.
/// </summary>
public sealed record DecompiledMemberResult(string Text, int Line, string Title);

/// <summary>
/// Decompiles the type an <see cref="IDefinitionResult"/> points at (TODO.md #62, "Go to Definition"/
/// ILSpy integration, user request 2026-07-19) and locates the specific member's declaration line
/// within the decompiled text, so the caller can open a read-only tab already scrolled to the right
/// place — mirrors dnSpy/ILSpy's own F12 behavior, minus the ability to keep following references
/// recursively inside the decompiled view itself (not yet implemented, see TODO.md).
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

        var settings = new DecompilerSettings();
        var peFile = new PEFile(assemblyPath);
        // The default single-arg CSharpDecompiler(path, settings) ctor builds its own resolver, but
        // that resolver's search-path heuristics (well-known install locations, DOTNET_ROOT, etc.)
        // don't find every real-world .NET install — e.g. dotnet installed via linuxbrew at a
        // nonstandard prefix. Adding THIS process's own runtime directory explicitly (the compiled
        // script runs against the very same installed runtime) is a robust fallback either way.
        var resolver = new UniversalAssemblyResolver(
            assemblyPath, throwOnError: false, peFile.DetectTargetFrameworkId());
        resolver.AddSearchDirectory(RuntimeEnvironment.GetRuntimeDirectory());
        var decompiler = new CSharpDecompiler(assemblyPath, resolver, settings);

        var fullTypeName = new FullTypeName(typeName);
        var typeDef = decompiler.TypeSystem.FindType(fullTypeName).GetDefinition();
        if (typeDef is null)
        {
            return new DecompiledMemberResult(
                $"// Could not find type '{typeName}' in '{assemblyPath}'.", 1, definition.DisplayName);
        }

        var syntaxTree = decompiler.DecompileType(fullTypeName);
        var stringWriter = new StringWriter();
        var tokenWriter = TokenWriter.CreateWriterThatSetsLocationsInAST(stringWriter);
        var visitor = new CSharpOutputVisitor(tokenWriter, settings.CSharpFormattingOptions);
        syntaxTree.AcceptVisitor(visitor);
        var text = stringWriter.ToString();

        ISymbol? target = definition.MemberName is { } memberName
            ? FindMember(typeDef, memberName, definition.ParameterTypeNames)
            : typeDef;

        var line = 1;
        if (target is not null)
        {
            foreach (var node in syntaxTree.Descendants.OfType<EntityDeclaration>())
            {
                if (!Equals(node.GetSymbol(), target)) continue;
                line = node.StartLocation.Line;
                break;
            }
        }

        return new DecompiledMemberResult(text, line, definition.DisplayName);
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
}
