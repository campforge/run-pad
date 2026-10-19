using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Markdig;
using MarkView.Avalonia;
using MarkView.Avalonia.Rendering;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// The MarkView fork's mapping between rendered blocks and source lines, which lets the preview
// scroll with the editor.
public class MarkdownViewerSourceLineTests
{
    private const string Document = "# Title\n\nOne\ntwo\n\n```cs\na\nb\nc\n```\n\n- x\n- y\n";

    [Fact]
    public void Render_Should_RecordTheLinesOfEachTopLevelBlock_AndOfTheCodeAlone()
    {
        var renderer = new AvaloniaRenderer();
        renderer.Render(Markdown.Parse(Document));

        Assert.Equal(
            [(0, 2), (2, 5), (6, 9), (11, 13)], // blank lines belong to the block before them, except after code,
            renderer.SourceBlocks.Select(b => (b.StartLine, b.EndLine)));
        Assert.IsType<TextBlock>(renderer.SourceBlocks[2].Control);
    }

    [AvaloniaFact]
    public void ScrollToSourceLine_Should_PutTheLineAtTheTop_AndGetSourceLineAtTop_ReadItBack()
    {
        var code = string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i}\n"));
        var viewer = new MarkdownViewer { Markdown = $"# Title\n\nText.\n\n```\n{code}```\n" };
        var window = new Window { Width = 400, Height = 200, Content = viewer };
        window.Show();
        try
        {
            var scrolls = 0;
            viewer.ScrollChanged += (_, _) => scrolls++;

            viewer.ScrollToSourceLine(5 + 40.5); // the code starts on line 5
            window.UpdateLayout();

            Assert.Equal(45.5, viewer.GetSourceLineAtTop()!.Value, 3);
            Assert.True(scrolls > 0);

            viewer.ScrollToSourceLine(0);
            window.UpdateLayout();
            Assert.Equal(0, viewer.GetSourceLineAtTop());
        }
        finally
        {
            window.Close();
        }
    }

    // ── Lazy code blocks ──────────────────────────────────────────────────────

    private static string Code(int lines) => string.Concat(Enumerable.Range(0, lines).Select(i => $"line {i}\n"));

    private static (MarkdownViewer Viewer, Window Window) ShowLazy(string markdown)
    {
        var viewer = new MarkdownViewer { LazyCodeBlocks = true, Markdown = markdown };
        var window = new Window { Width = 400, Height = 200, Content = viewer };
        window.Show();
        window.UpdateLayout();
        return (viewer, window);
    }

    private static LazyCodeLines LazyLines(MarkdownViewer viewer)
        => global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(viewer).OfType<LazyCodeLines>().Single();

    [AvaloniaFact]
    public void ALazyCodeBlock_Should_FillOnlyTheChunksNearTheViewport_AndTheOthersOnceScrolledTo()
    {
        var (viewer, window) = ShowLazy($"```cs\n{Code(1000)}```\n");
        try
        {
            var lazy = LazyLines(viewer);
            Assert.Equal(10, lazy.Chunks.Count());
            Assert.True(lazy.IsFilled(0));
            Assert.False(lazy.IsFilled(9));
            Assert.Equal("line 900", lazy.Chunks.Last().Text!.Split('\n')[0]); // plain text until filled

            viewer.ScrollToSourceLine(950);
            window.UpdateLayout();

            Assert.True(lazy.IsFilled(9));
            Assert.False(lazy.IsFilled(5));
            Assert.Equal(950, viewer.GetSourceLineAtTop()!.Value, 3);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ALazyCodeBlock_Should_StillCopyAllItsLines()
    {
        var (viewer, window) = ShowLazy($"```cs\n{Code(250)}```\n");
        try
        {
            viewer.SelectAll();

            Assert.Equal(Code(250).TrimEnd('\n'), viewer.GetSelectedText().ReplaceLineEndings("\n"));
        }
        finally
        {
            window.Close();
        }
    }
}
