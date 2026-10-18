// Copyright (c) Nicolas Musset
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

using Markdig.Syntax;

namespace MarkView.Avalonia.Rendering.Blocks;

public sealed class CodeBlockRenderer : AvaloniaObjectRenderer<CodeBlock>
{
    protected override void Write(AvaloniaRenderer renderer, CodeBlock obj)
    {
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
        };

        var border = new Border
        {
            Child = textBlock,
        };
        border.Classes.Add("markdown-code-block");

        if (obj is FencedCodeBlock fenced && !string.IsNullOrEmpty(fenced.Info))
        {
            border.Classes.Add($"language-{fenced.Info}");
        }

        if (renderer.LazyCodeBlocks && obj.Lines.Count > 0)
        {
            var lines = new List<ReadOnlyMemory<char>>(obj.Lines.Count);
            for (int i = 0; i < obj.Lines.Count; i++)
            {
                var slice = obj.Lines.Lines[i].Slice;
                lines.Add(slice.Text.AsMemory(slice.Start, slice.Length));
            }
            border.Child = new LazyCodeLines(lines, static (inlines, chunk) =>
            {
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (i > 0) inlines.Add(new LineBreak());
                    inlines.Add(new Run(chunk[i].ToString()));
                }
            });
            renderer.WriteBlock(border);
            return;
        }

        renderer.Push(textBlock.Inlines!);
        renderer.WriteLeafRawLines(obj);
        renderer.Pop();

        renderer.WriteBlock(border);
    }
}
