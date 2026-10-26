namespace RunPad.Contracts.IntelliSense;

// What an F12 press at a caret
// position resolves to. Scripts are single-file, so an in-source result is just an offset to move
// the caret to in the SAME document — no cross-file navigation needed. A symbol declared outside the
// script (framework/NuGet metadata) has no source to jump to; instead this carries enough identifying
// info to decompile it on demand. Plain strings rather than Roslyn/decompiler types because the
// decompiler lives in RunPad.Languages.DotnetCommon, which has no (and shouldn't take a) reference to
// Microsoft.CodeAnalysis.
public interface IDefinitionResult
{
    bool IsInSource { get; }

    /// <summary>Offset to move the caret to in the current document. Meaningful only when <see cref="IsInSource"/>.</summary>
    int Offset { get; }

    /// <summary>Path to the assembly containing the symbol's metadata. Meaningful only when NOT <see cref="IsInSource"/>.</summary>
    string? AssemblyPath { get; }

    /// <summary>Reflection-style full name (Namespace.Type+Nested) of the symbol's containing type — or of the symbol itself, if it IS a type. Meaningful only when NOT <see cref="IsInSource"/>.</summary>
    string? ContainingTypeFullName { get; }

    /// <summary>Null when the definition IS the containing type itself (<see cref="ContainingTypeFullName"/> already identifies it).</summary>
    string? MemberName { get; }

    /// <summary>Reflection-style parameter type names, in declaration order — disambiguates overloads. Empty for non-methods/constructors.</summary>
    IReadOnlyList<string> ParameterTypeNames { get; }

    /// <summary>Human-readable label for a decompiled-view tab header, e.g. "List&lt;T&gt;.Add(T)".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Set when the symbol is declared in a different, already-generated source document of this
    /// same project -- a connection's generated message/client class, say -- rather than the current
    /// editor's own document (<see cref="IsInSource"/> is false then too) or external metadata
    /// (<see cref="AssemblyPath"/> is null here). <c>Line</c> is 1-based, matching the decompiler's
    /// own convention. Defaults to null so existing implementations need no change.
    /// </summary>
    (string Text, int Line)? GeneratedDocument => null;
}
