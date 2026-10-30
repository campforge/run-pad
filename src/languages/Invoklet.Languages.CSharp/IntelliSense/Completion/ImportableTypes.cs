using System.Collections.Concurrent;
using Invoklet.Languages.CSharp.IntelliSense.ScriptDocument;
using Microsoft.CodeAnalysis;

namespace Invoklet.Languages.CSharp.IntelliSense.Completion;

/// <summary>The types of the referenced assemblies a name could be completed to by adding a using.</summary>
internal static class ImportableTypes
{
    // Types from the script's references whose simple name starts with what's typed so far and
    // isn't already visible: packages and the script's own assemblies from the first letter, the
    // framework from MinFrameworkImportPrefix letters, so a letter or two doesn't list half the
    // framework. Types that share a name are all offered, each with its own namespace: the user
    // picks which `using` to add, none is chosen for them.
    internal static IEnumerable<INamedTypeSymbol> FindImportableTypes(
        ScriptContext context,
        Compilation compilation, string prefix, HashSet<string> alreadyVisible)
    {
        if (prefix.Length == 0) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in compilation.References)
        {
            if (prefix.Length < MinFrameworkImportPrefix && !context.IsAddedReference(reference)) continue;
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;

            foreach (var entry in GetImportableTypeIndex(assembly, reference))
            {
                if (alreadyVisible.Contains(entry.Name)) continue;
                if (!entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                // The same type can come from two references (a package that also ships in the framework).
                if (!seen.Add(entry.MetadataName)) continue;

                var type = assembly.GetTypeByMetadataName(entry.MetadataName);
                if (type == null || IsHiddenFromEditor(type)) continue;
                yield return type;
            }
        }
    }

    internal const int MinFrameworkImportPrefix = 3;

    // [EditorBrowsable(Never)]: public for technical reasons, not meant to be written by hand.
    internal static bool IsHiddenFromEditor(ISymbol symbol) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.Name == nameof(System.ComponentModel.EditorBrowsableAttribute)
            && a.ConstructorArguments is [{ Value: int state }] && state == (int)System.ComponentModel.EditorBrowsableState.Never);

    // Flattened index of every public importable type in a referenced assembly, built once per
    // reference and cached for the process lifetime (shared across every tab/service instance, same
    // sharing rationale as ReferenceCache — a MetadataReference is immutable, so its type index is
    // too). Walking the whole namespace tree (EnumerateNamespaceTypes) on every completion keystroke
    // made latency scale with the assembly's total public-type count; this makes each request a cheap
    // prefix scan over already-flattened plain strings instead. Entries store the metadata name
    // rather than the symbol itself, since INamedTypeSymbol instances aren't safe to reuse across
    // different Compilation instances — FindImportableTypes re-resolves only the (much smaller,
    // prefix-matched) subset via IAssemblySymbol.GetTypeByMetadataName against the current compilation.
    internal static readonly ConcurrentDictionary<MetadataReference, IReadOnlyList<ImportableTypeEntry>>
        _importableTypeIndexCache = new();

    internal readonly record struct ImportableTypeEntry(string Name, string MetadataName);

    internal static IReadOnlyList<ImportableTypeEntry> GetImportableTypeIndex(
        IAssemblySymbol assembly, MetadataReference reference)
    {
        if (_importableTypeIndexCache.TryGetValue(reference, out var cached)) return cached;

        var entries = EnumerateNamespaceTypes(assembly.GlobalNamespace)
            .Select(t => new ImportableTypeEntry(t.Name, GetFullMetadataName(t)))
            .ToArray();
        return _importableTypeIndexCache.GetOrAdd(reference, entries);
    }

    internal static string GetFullMetadataName(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace;
        return ns is { IsGlobalNamespace: false } ? $"{ns.ToDisplayString()}.{type.MetadataName}" : type.MetadataName;
    }

    internal static IEnumerable<INamedTypeSymbol> EnumerateNamespaceTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol childNamespace:
                    foreach (var type in EnumerateNamespaceTypes(childNamespace))
                        yield return type;
                    break;
                case INamedTypeSymbol { DeclaredAccessibility: Accessibility.Public } type:
                    yield return type;
                    break;
            }
        }
    }
}
