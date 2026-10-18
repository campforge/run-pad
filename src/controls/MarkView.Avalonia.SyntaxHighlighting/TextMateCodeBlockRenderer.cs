// Copyright (c) Nicolas Musset
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;

using Markdig.Syntax;

using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;
using MarkView.Avalonia.Rendering.Blocks;

namespace MarkView.Avalonia.SyntaxHighlighting;

/// <summary>
/// Replaces the built-in <see cref="CodeBlockRenderer"/> when syntax highlighting is active.
/// For fenced code blocks it calls <see cref="IThemeAwareCodeHighlighter.HighlightVariant"/>
/// and subscribes to theme changes so token colours update in-place when the user switches
/// between light and dark themes — without rebuilding the surrounding document tree.
/// </summary>
public sealed class TextMateCodeBlockRenderer : AvaloniaObjectRenderer<CodeBlock>
{
    protected override void Write(AvaloniaRenderer renderer, CodeBlock obj)
    {
        var language = obj is FencedCodeBlock fenced ? fenced.Info : null;

        var textBlock = new TextBlock { TextWrapping = TextWrapping.NoWrap };
        var border = new Border { Child = textBlock };
        border.Classes.Add("markdown-code-block");

        if (!string.IsNullOrEmpty(language))
            border.Classes.Add($"language-{language}");

        if (obj.Lines.Lines == null)
        {
            renderer.WriteBlock(border);
            return;
        }

        // Materialise source lines once so they can be reused on theme change.
        // AsMemory() references the Markdig source buffer directly — no per-line allocation.
        var lineTexts = new List<ReadOnlyMemory<char>>(obj.Lines.Count);
        for (int i = 0; i < obj.Lines.Count; i++)
        {
            var slice = obj.Lines.Lines[i].Slice;
            lineTexts.Add(slice.Text.AsMemory(slice.Start, slice.Length));
        }

        var highlighter = renderer.CodeHighlighter;
        LazyCodeLines? lazy = null;
        if (renderer.LazyCodeBlocks)
        {
            // Each chunk is highlighted in the theme current when it is filled.
            border.Child = lazy = new LazyCodeLines(lineTexts, (inlines, chunk) => BuildInlines(
                inlines, highlighter, language, Application.Current?.ActualThemeVariant == ThemeVariant.Dark, chunk));
        }
        else
        {
            var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
            BuildInlines(textBlock.Inlines!, highlighter, language, isDark, lineTexts);
        }

        // If the highlighter is theme-aware, rebuild only TextBlock.Inlines when the
        // theme changes — the Border and its position in the document stay untouched.
        if (renderer.CodeHighlighter is IThemeAwareCodeHighlighter themeAware)
        {
            void OnThemeChanged(object? s, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property.Name != nameof(Application.ActualThemeVariant)) return;
                if (lazy is not null)
                {
                    lazy.Refill();
                    return;
                }
                var newIsDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
                textBlock.Inlines!.Clear();
                BuildInlines(textBlock.Inlines!, themeAware, language, newIsDark, lineTexts);
            }
            Application.Current!.PropertyChanged += OnThemeChanged;
            border.DetachedFromLogicalTree += (_, _) => Application.Current?.PropertyChanged -= OnThemeChanged;
        }

        renderer.WriteBlock(border);
    }

    private static void BuildInlines(
        InlineCollection inlines,
        ICodeHighlighter? highlighter,
        string? language,
        bool isDark,
        IReadOnlyList<ReadOnlyMemory<char>> lineTexts)
    {
        for (int i = 0; i < lineTexts.Count; i++)
        {
            if (i > 0) inlines.Add(new LineBreak());

            var lineText = lineTexts[i];
            var tokens = highlighter is IThemeAwareCodeHighlighter th
                ? th.HighlightVariant(lineText, language, isDark)
                : highlighter?.Highlight(lineText, language);

            if (tokens != null)
            {
                foreach (var (text, foreground) in tokens)
                {
                    var run = new Run(text);
                    if (foreground != null)
                        run.Foreground = foreground;
                    inlines.Add(run);
                }
            }
            else
            {
                inlines.Add(new Run(lineText.ToString()));
            }
        }
    }
}
