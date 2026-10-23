using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using RunPad.Avalonia.Themes;
using Xunit;

namespace RunPad.Avalonia.HtmlRenderer.Tests;

// The renderer supports only a subset of CSS (no overflow-wrap), so wrapping is checked on frames
// rendered from the results stylesheet, not on the generated CSS text.
[Collection(SkiaHeadlessCollection.Name)]
public class ResultsWrapLayoutTests
{
    private const double ViewportWidth = 300;
    private const string LongToken = "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX";

    private readonly SkiaHeadlessSessionFixture _fixture;

    public ResultsWrapLayoutTests(SkiaHeadlessSessionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public Task LongToken_Should_BreakInsideTheViewport_WhenWrapping()
        => _fixture.Session.Dispatch(() =>
        {
            var single = Ink("<pre>X</pre>", wrap: true);
            var wrapped = Ink($"<pre>{LongToken}</pre>", wrap: true);

            Assert.True(wrapped.Height > single.Height * 2, "the token should span several lines");
            Assert.True(wrapped.Right < ViewportWidth - 4, $"the token should end inside the viewport, ink {wrapped}");
        }, CancellationToken.None);

    [Fact]
    public Task LongToken_Should_OverflowHorizontally_OnlyWhenNotWrapping()
        => _fixture.Session.Dispatch(() =>
        {
            Assert.True(NeedsHorizontalScroll($"<pre>{LongToken}</pre>", wrap: false));
            Assert.False(NeedsHorizontalScroll($"<pre>{LongToken}</pre>", wrap: true));
        }, CancellationToken.None);

    [Fact]
    public Task ExplicitLineBreaks_Should_BePreserved_WhenWrapping()
        => _fixture.Session.Dispatch(() =>
        {
            var one = Ink("<pre>XXX</pre>", wrap: true);
            var three = Ink("<pre>XXX\nXXX\nXXX</pre>", wrap: true);

            Assert.True(three.Height > one.Height * 2.5, $"expected three lines, ink {three} vs {one}");
        }, CancellationToken.None);

    [Fact]
    public Task Spaces_Should_BePreserved_WhenWrapping()
        => _fixture.Session.Dispatch(() =>
        {
            var narrow = Ink("<pre>X X</pre>", wrap: true);
            var wide = Ink("<pre>X          X</pre>", wrap: true);

            Assert.Equal(narrow.Height, wide.Height);
            Assert.True(wide.Width > narrow.Width + 30, $"spaces collapsed, ink {wide} vs {narrow}");
        }, CancellationToken.None);

    private static Rect Ink(string html, bool wrap)
    {
        var (window, _) = Show(html, wrap);
        try
        {
            return DarkExtent(window.CaptureRenderedFrame()!);
        }
        finally
        {
            window.Close();
        }
    }

    // The panel shows its horizontal scroll bar exactly when the laid-out content is wider than the viewport.
    private static bool NeedsHorizontalScroll(string html, bool wrap)
    {
        var (window, panel) = Show(html, wrap);
        try
        {
            return panel.GetVisualDescendants().OfType<ScrollBar>()
                .Single(s => s.Orientation == Orientation.Horizontal).Visibility == ScrollBarVisibility.Visible;
        }
        finally
        {
            window.Close();
        }
    }

    private static (Window Window, HtmlPanel Panel) Show(string html, bool wrap)
    {
        var panel = new HtmlPanel
        {
            Background = Brushes.White,
            BaseStylesheet = HtmlThemeStylesheet.Generate(ThemeVariant.Light, isDark: false, wrapLongLines: wrap),
            Text = html,
        };
        var window = new Window { Width = ViewportWidth, Height = 400, Background = Brushes.White, Content = panel };
        window.Show();
        window.CaptureRenderedFrame();
        return (window, panel);
    }

    // Bounding box of the dark glyph pixels (the pre background is light grey, the text dark grey).
    private static Rect DarkExtent(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var pointer = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), pointer, buffer.Length, stride);
            Marshal.Copy(pointer, buffer, 0, buffer.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var i = 0; i < buffer.Length; i += 4)
        {
            if (buffer[i] < 128 && buffer[i + 1] < 128 && buffer[i + 2] < 128)
            {
                var x = i / 4 % size.Width;
                var y = i / 4 / size.Width;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        Assert.True(maxX >= 0, "nothing was rendered");
        return new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
