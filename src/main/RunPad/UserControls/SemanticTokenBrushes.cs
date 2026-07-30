using Avalonia.Media;
using RunPad.Contracts.IntelliSense;

namespace RunPad.UserControls;

// TODO: Это все имеет какое то отношение к стеме и стялям, адо перенести в темы
// The single source of truth for SemanticTokenKind → color, shared by SemanticHighlighter (live
// editor text) and by anything rendering a DisplayTextSegment signature outside the main text view
// (completion/signature-help/quick-info tooltips — see ANALYSIS.md TODO #50). Keeping one palette
// means a tooltip's syntax colors always match the editor's, without a second color table to drift.
internal static class SemanticTokenBrushes
{
    public static IBrush? GetBrush(SemanticTokenKind kind, bool isDark) => isDark ? GetDarkBrush(kind) : GetLightBrush(kind);

    // VS Code Dark+ palette
    private static IBrush? GetDarkBrush(SemanticTokenKind kind) => kind switch
    {
        SemanticTokenKind.ClassName or SemanticTokenKind.StructName or
        SemanticTokenKind.EnumName or SemanticTokenKind.DelegateName or
        SemanticTokenKind.TypeParameterName
            => new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)),
        SemanticTokenKind.InterfaceName
            => new SolidColorBrush(Color.FromRgb(0xB8, 0xD7, 0xA3)),
        SemanticTokenKind.MethodName or SemanticTokenKind.ExtensionMethodName
            => new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA)),
        SemanticTokenKind.PropertyName or SemanticTokenKind.FieldName or
        SemanticTokenKind.EventName or SemanticTokenKind.NamespaceName
            => new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)),
        SemanticTokenKind.Keyword
            => new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)),
        _ => null,
    };

    // VS 2022 Light palette
    private static IBrush? GetLightBrush(SemanticTokenKind kind) => kind switch
    {
        SemanticTokenKind.ClassName or SemanticTokenKind.StructName or
        SemanticTokenKind.EnumName or SemanticTokenKind.DelegateName or
        SemanticTokenKind.TypeParameterName or SemanticTokenKind.InterfaceName
            => new SolidColorBrush(Color.FromRgb(0x26, 0x7F, 0x99)),
        SemanticTokenKind.MethodName or SemanticTokenKind.ExtensionMethodName
            => new SolidColorBrush(Color.FromRgb(0x79, 0x5E, 0x26)),
        SemanticTokenKind.PropertyName or SemanticTokenKind.FieldName or
        SemanticTokenKind.EventName or SemanticTokenKind.NamespaceName
            => new SolidColorBrush(Color.FromRgb(0x00, 0x10, 0x80)),
        SemanticTokenKind.Keyword
            => new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0xFF)),
        _ => null,
    };
}
