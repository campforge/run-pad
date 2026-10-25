using System.Text.RegularExpressions;
using AvaloniaEdit.Snippets;

namespace RunPad.Avalonia.CodeTextEditor;

/// <summary>
/// Turns a snippet template with fields — <c>${1:int}</c>, <c>${2:Property}</c> — into an
/// AvaloniaEdit <see cref="Snippet"/>, whose fields Tab and Shift+Tab move between and Enter or
/// Escape leaves. <c>${0}</c> marks where the caret goes when Enter ends the snippet. A template
/// without numbered fields keeps the plain one-step expansion in
/// <see cref="CodeTextEditor.ExpandSnippet"/>.
/// </summary>
internal static partial class SnippetTemplate
{
    [GeneratedRegex(@"\$\{(\d+)(?::([^}]*))?\}")]
    private static partial Regex Marker();

    /// <summary>The text the template inserts, fields showing their default text: for a preview.</summary>
    public static string Preview(string template)
        => Marker().Replace(template, m => m.Groups[2].Value);

    /// <summary>Null when <paramref name="template"/> has no field other than <c>${0}</c>.</summary>
    public static Snippet? TryCreate(string template)
    {
        var markers = Marker().Matches(template);
        if (!markers.Any(m => m.Groups[1].Value != "0")) return null;

        var snippet = new Snippet();
        var position = 0;
        foreach (Match marker in markers)
        {
            if (marker.Index > position)
                snippet.Elements.Add(new SnippetTextElement { Text = template[position..marker.Index] });
            snippet.Elements.Add(marker.Groups[1].Value == "0"
                ? new SnippetCaretElement()
                : new SnippetReplaceableTextElement { Text = marker.Groups[2].Value });
            position = marker.Index + marker.Length;
        }
        if (position < template.Length)
            snippet.Elements.Add(new SnippetTextElement { Text = template[position..] });
        return snippet;
    }
}
