using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using RunPad.Contracts.IntelliSense;

namespace RunPad.UserControls;

// Renders a DisplayTextSegment signature (syntax-highlighted, using SemanticTokenBrushes) plus an
// optional plain-text XML-doc summary as a real Avalonia control. AvaloniaEdit's
// ICompletionData.Description, IOverloadProvider.CurrentHeader/CurrentContent and ToolTip.Tip are all
// typed `object` and rendered by a bare ContentControl, so handing them a Control instead of a string
// is enough to get rich tooltips — no AvaloniaEdit fork needed.
internal static class DisplaySegmentRenderer
{
    public static TextBlock BuildSignature(IReadOnlyList<DisplayTextSegment> signature, bool isDark)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Inlines = new InlineCollection() };
        foreach (var segment in signature)
        {
            var run = new Run(segment.Text);
            var brush = SemanticTokenBrushes.GetBrush(segment.Kind, isDark);
            if (brush is not null) run.Foreground = brush;
            if (segment.Emphasis) run.FontWeight = FontWeight.Bold;
            text.Inlines.Add(run);
        }
        return text;
    }

    public static Control BuildWithSummary(IReadOnlyList<DisplayTextSegment> signature, string? summary, bool isDark)
    {
        var signatureBlock = BuildSignature(signature, isDark);
        if (string.IsNullOrEmpty(summary)) return signatureBlock;

        return new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            MaxWidth = 480,
            Children =
            {
                signatureBlock,
                new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 },
            },
        };
    }

    // The completion list row itself (not the Description tooltip) — plain displayText, or displayText
    // plus a dimmed annotation: "(in Detail)" for a not-yet-imported type (
    // ICompletionItem.Detail), or "(+N overloads)" when several overloads were collapsed down to this
    // one item (ICompletionItem.OverloadCount) — the two never co-occur (Detail
    // is only ever set for importable types, OverloadCount > 1 only for methods), so at most one of
    // them applies. isOwnMember (ICompletionItem.IsOwnMember) is orthogonal
    // to both — a member declared directly on the type being completed renders bold regardless of
    // whether it also happens to carry one of the other two annotations. Same
    // object-typed-Content-is-a-bare-ContentControl trick as BuildWithSummary/BuildSignature above,
    // applied to EditorCompletionData.Content instead.
    public static Control BuildListRow(string displayText, string? detail, int overloadCount, bool isOwnMember = false)
    {
        var nameBlock = new TextBlock
        {
            Text = displayText,
            FontWeight = isOwnMember ? FontWeight.Bold : FontWeight.Normal,
        };
        var hiddenOverloads = overloadCount - 1;

        var annotation = overloadCount > 1 ? $"(+{hiddenOverloads} overload{(hiddenOverloads == 1 ? "" : "s")})"

            : string.IsNullOrEmpty(detail) ? null : $"(in {detail})";
        if (annotation is null) return nameBlock;

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                nameBlock,
                new TextBlock { Text = annotation, Opacity = 0.6, FontStyle = FontStyle.Italic },
            },
        };
    }
}
