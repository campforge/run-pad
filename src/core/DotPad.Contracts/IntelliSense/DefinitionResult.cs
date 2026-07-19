namespace DotPad.Contracts.IntelliSense;

// TODO.md #62 "Go to Definition" (F12, user request 2026-07-19): what an F12 press at a caret
// position resolves to. Scripts are single-file, so an in-source result is just an offset to move
// the caret to in the SAME document — no cross-file navigation needed. A symbol declared outside the
// script (framework/NuGet metadata) has no source to jump to; instead this carries enough identifying
// info to decompile it on demand. Plain strings rather than Roslyn/decompiler types because the
// decompiler lives in DotPad.Languages.DotnetCommon, which has no (and shouldn't take a) reference to
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
}
