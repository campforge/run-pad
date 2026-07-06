using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using DotPad.Contracts.IntelliSense;

namespace DotPad.UserControls;

internal sealed class SemanticHighlighter : DocumentColorizingTransformer
{
    private IReadOnlyList<ISemanticToken> _tokens = [];
    private bool _isDark = true;

    public void Update(IReadOnlyList<ISemanticToken> tokens) => _tokens = tokens;
    public void SetDark(bool isDark) => _isDark = isDark;

    protected override void ColorizeLine(DocumentLine line)
    {
        var tokens = _tokens;
        if (tokens.Count == 0) return;

        var lineStart = line.Offset;
        var lineEnd = line.EndOffset;

        foreach (var token in tokens)
        {
            var start = token.StartOffset;
            var end = start + token.Length;
            if (end <= lineStart || start >= lineEnd) continue;

            var brush = _isDark ? GetDarkBrush(token.Kind) : GetLightBrush(token.Kind);
            if (brush is null) continue;

            var s = Math.Max(start, lineStart);
            var e = Math.Min(end, lineEnd);
            ChangeLinePart(s, e, el => el.TextRunProperties.SetForegroundBrush(brush));
        }
    }

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
        _ => null,
    };
}
