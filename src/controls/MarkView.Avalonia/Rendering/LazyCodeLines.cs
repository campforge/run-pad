// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MarkView.Avalonia.Rendering;

/// <summary>
/// The lines of a code block in chunks of <see cref="LinesPerChunk"/>, each a plain-text
/// <see cref="TextBlock"/> until it comes near the viewport, when its highlighted inlines are built.
/// A long block then costs only its text up front, not its highlighting, and no single text block
/// holds thousands of runs. The plain text has the same lines and characters as the highlighted one,
/// so heights, scrolling and selection do not change when a chunk is highlighted.
/// </summary>
public sealed class LazyCodeLines : StackPanel
{
    /// <summary>The number of lines in each chunk.</summary>
    public const int LinesPerChunk = 100;

    private readonly IReadOnlyList<ReadOnlyMemory<char>> _lines;
    private readonly Action<InlineCollection, IReadOnlyList<ReadOnlyMemory<char>>> _fill;
    private readonly bool[] _filled;

    /// <param name="lines">The code's lines, without line breaks.</param>
    /// <param name="fill">Adds the inlines for the given lines, with a <see cref="LineBreak"/>
    /// between each two, to a chunk's empty inline collection.</param>
    public LazyCodeLines(
        IReadOnlyList<ReadOnlyMemory<char>> lines,
        Action<InlineCollection, IReadOnlyList<ReadOnlyMemory<char>>> fill)
    {
        _lines = lines;
        _fill = fill;
        _filled = new bool[(lines.Count + LinesPerChunk - 1) / LinesPerChunk];
        for (var i = 0; i < _filled.Length; i++)
        {
            var chunk = new TextBlock
            {
                TextWrapping = TextWrapping.NoWrap,
                Text = ChunkText(i),
            };
            var index = i;
            chunk.EffectiveViewportChanged += (_, e) =>
            {
                // A viewport's height above and below is filled ahead of scrolling into it.
                if (!_filled[index] && e.EffectiveViewport.Inflate(new Thickness(0, e.EffectiveViewport.Height))
                        .Intersects(new Rect(chunk.Bounds.Size)))
                    Fill(index);
            };
            Children.Add(chunk);
        }
    }

    /// <summary>The chunks' text blocks, top to bottom.</summary>
    public IEnumerable<TextBlock> Chunks => Children.Cast<TextBlock>();

    /// <summary>The plain text of the chunk at <paramref name="index"/>, its lines joined by "\n".</summary>
    public string ChunkText(int index) => string.Join("\n", ChunkLines(index).Select(l => l.ToString()));

    /// <summary>Whether the chunk at <paramref name="index"/> has its highlighted inlines.</summary>
    public bool IsFilled(int index) => _filled[index];

    /// <summary>
    /// Builds the inlines again for the chunks that have them, such as after a theme change; the
    /// others take the current state when they are filled.
    /// </summary>
    public void Refill()
    {
        for (var i = 0; i < _filled.Length; i++)
        {
            if (_filled[i])
                Fill(i);
        }
    }

    private IReadOnlyList<ReadOnlyMemory<char>> ChunkLines(int index)
    {
        var start = index * LinesPerChunk;
        return _lines.Skip(start).Take(Math.Min(LinesPerChunk, _lines.Count - start)).ToList();
    }

    private void Fill(int index)
    {
        var chunk = (TextBlock)Children[index];
        var inlines = new InlineCollection();
        _fill(inlines, ChunkLines(index));
        chunk.Text = null;
        chunk.Inlines = inlines;
        _filled[index] = true;
    }
}
