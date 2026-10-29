using Microsoft.CodeAnalysis;
using Invoklet.Contracts.IntelliSense;

namespace Invoklet.Languages.CSharp.IntelliSense;

// Converts Roslyn's classified SymbolDisplayPart stream into DisplayTextSegment, the Invoklet-Contracts
// type completion/signature-help/quick-info use to render syntax-highlighted signatures in tooltips
// — SemanticTokenKind is already the editor's own classification enum, so
// tooltips end up using the exact same palette as the live editor (see SemanticTokenBrushes).
internal static class SignatureFormatting
{
    // emphasize marks the DisplayTextSegment for the part whose originating symbol is that parameter
    // (SymbolDisplayPart.Symbol), so a signature-help header can bold the currently-active parameter.
    public static IReadOnlyList<DisplayTextSegment> ToSegments(
        this IEnumerable<SymbolDisplayPart> parts, ISymbol? emphasize = null) =>
        parts.Select(p => new DisplayTextSegment(p.ToString(), ToKind(p.Kind),
            emphasize is not null && SymbolEqualityComparer.Default.Equals(p.Symbol, emphasize))).ToList();

    private static SemanticTokenKind ToKind(SymbolDisplayPartKind kind) => kind switch
    {
        SymbolDisplayPartKind.Keyword => SemanticTokenKind.Keyword,
        SymbolDisplayPartKind.ClassName => SemanticTokenKind.ClassName,
        SymbolDisplayPartKind.StructName => SemanticTokenKind.StructName,
        SymbolDisplayPartKind.InterfaceName => SemanticTokenKind.InterfaceName,
        SymbolDisplayPartKind.EnumName => SemanticTokenKind.EnumName,
        SymbolDisplayPartKind.DelegateName => SemanticTokenKind.DelegateName,
        SymbolDisplayPartKind.TypeParameterName => SemanticTokenKind.TypeParameterName,
        SymbolDisplayPartKind.MethodName => SemanticTokenKind.MethodName,
        SymbolDisplayPartKind.ExtensionMethodName => SemanticTokenKind.ExtensionMethodName,
        SymbolDisplayPartKind.PropertyName => SemanticTokenKind.PropertyName,
        SymbolDisplayPartKind.FieldName => SemanticTokenKind.FieldName,
        SymbolDisplayPartKind.EventName => SemanticTokenKind.EventName,
        SymbolDisplayPartKind.NamespaceName => SemanticTokenKind.NamespaceName,
        SymbolDisplayPartKind.LocalName => SemanticTokenKind.LocalName,
        SymbolDisplayPartKind.ParameterName => SemanticTokenKind.ParameterName,
        _ => SemanticTokenKind.None,
    };
}
