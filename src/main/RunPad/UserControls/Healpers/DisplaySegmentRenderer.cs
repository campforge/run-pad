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
            text.Inlines.Add(BuildRun(segment, isDark));
        return text;
    }

    // Parameter info's signature, at most maxWidth wide. One that would not fit on a line of that
    // width (at charWidth per character of the editor's monospace font) puts each parameter on a
    // line of its own, so it breaks between parameters rather than inside a type name.
    public static TextBlock BuildParameterInfoSignature(
        IReadOnlyList<DisplayTextSegment> signature, bool isDark, double maxWidth, double charWidth)
    {
        var length = signature.Sum(s => s.Text.Length);
        if (length * charWidth <= maxWidth || SplitParameters(signature) is not { } split)
        {
            var oneLine = BuildSignature(signature, isDark);
            oneLine.MaxWidth = maxWidth;
            return oneLine;
        }

        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth, Inlines = new InlineCollection() };
        foreach (var segment in split.Head)
            text.Inlines.Add(BuildRun(segment, isDark));
        for (var i = 0; i < split.Parameters.Count; i++)
        {
            text.Inlines.Add(new LineBreak());
            text.Inlines.Add(new Run(ParameterIndent));
            foreach (var segment in split.Parameters[i])
                text.Inlines.Add(BuildRun(segment, isDark));
            if (i < split.Parameters.Count - 1) text.Inlines.Add(new Run(","));
        }
        text.Inlines.Add(new LineBreak());
        foreach (var segment in split.Tail)
            text.Inlines.Add(BuildRun(segment, isDark));
        return text;
    }

    private const string ParameterIndent = "    ";

    // A signature split around its parameter list: Head ends with the list's `(`, Tail starts with
    // its `)`, and Parameters holds each parameter without the commas between them. The list is
    // the last top-level parenthesized one, so a tuple in the return type is left alone; brackets
    // inside a parameter (generic arguments, tuples, arrays) keep its commas in it. Null when the
    // signature has no parameters to split.
    internal static (IReadOnlyList<DisplayTextSegment> Head, IReadOnlyList<IReadOnlyList<DisplayTextSegment>> Parameters,
        IReadOnlyList<DisplayTextSegment> Tail)? SplitParameters(IReadOnlyList<DisplayTextSegment> signature)
    {
        var close = -1;
        for (var i = signature.Count - 1; i >= 0; i--)
        {
            if (signature[i].Text == ")") { close = i; break; }
        }
        if (close < 0) return null;

        var open = -1;
        var depth = 0;
        for (var i = close; i >= 0; i--)
        {
            depth += BracketDepthChange(signature[i].Text);
            if (depth == 0) { open = i; break; }
        }
        if (open < 0 || close == open + 1) return null;

        var parameters = new List<IReadOnlyList<DisplayTextSegment>>();
        var current = new List<DisplayTextSegment>();
        depth = 0;
        for (var i = open + 1; i < close; i++)
        {
            var segment = signature[i];
            if (depth == 0 && segment.Text == ",")
            {
                parameters.Add(current);
                current = [];
                continue;
            }
            depth -= BracketDepthChange(segment.Text);
            // The space after a comma would start the parameter's own line.
            if (current.Count == 0 && string.IsNullOrWhiteSpace(segment.Text)) continue;
            current.Add(segment);
        }
        parameters.Add(current);

        return (signature.Take(open + 1).ToList(), parameters, signature.Skip(close).ToList());
    }

    // Scanning backwards: a closing bracket goes one level in, an opening one comes back out.
    private static int BracketDepthChange(string text) => text switch
    {
        ")" or ">" or "]" or "}" => 1,
        "(" or "<" or "[" or "{" => -1,
        _ => 0,
    };

    private static Run BuildRun(DisplayTextSegment segment, bool isDark)
    {
        var run = new Run(segment.Text);
        var brush = SemanticTokenBrushes.GetBrush(segment.Kind, isDark);
        if (brush is not null) run.Foreground = brush;
        if (segment.Emphasis) run.FontWeight = FontWeight.Bold;
        return run;
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
