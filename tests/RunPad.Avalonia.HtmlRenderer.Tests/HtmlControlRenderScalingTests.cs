using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Xunit;

namespace RunPad.Avalonia.HtmlRenderer.Tests;

// Live report: Results text/tables visibly grew when the window moved from an external monitor to
// the MacBook display (same window, same panel bounds). Two defects in HtmlControl's offscreen cache:
//  1. the cache ignored render scaling, so a scaling change kept blitting a bitmap rasterized for
//     the previous display (and nothing invalidated it — Bounds don't change);
//  2. the cache was a `96 * scaling` DPI RenderTargetBitmap drawn with DrawImage(bitmap, destRect)
//     from Render — Avalonia 12's compositor reads that DIP-sized source rect as device pixels, so
//     any cache painted at scaling 2 showed only its top-left quarter stretched over the control:
//     content 2x too large and clipped. Plain Skia RenderTargetBitmap drawing is otherwise correct.
// These tests capture real Skia-rendered frames (not headless drawing, which has no pixels) and
// check the content keeps its logical (DIP) geometry across scaling transitions at identical bounds.
// RunPad.Runtime.UI.Tests can't host this: its assembly-wide [AvaloniaTestFramework] session uses
// headless drawing and a second HeadlessUnitTestSession next to it deadlocks, so this project runs
// its own private Skia session instead.
public static class SkiaTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .AfterSetup(_ => Application.Current!.Styles.Add(new FluentTheme()));
}

public sealed class SkiaHeadlessSessionFixture : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(SkiaTestAppBuilder));

    public void Dispose() => Session.Dispose();
}

public class HtmlControlRenderScalingTests : IClassFixture<SkiaHeadlessSessionFixture>
{
    private const string BlockHtml = "<div style=\"width:50px;height:30px;background-color:#000000\"></div>";

    private const string TextHtml =
        "<div style=\"font-family:Arial;font-size:12px\">Results Value 12345 table</div>" +
        "<table border=1><tr><td>Id</td><td>Name</td></tr><tr><td>1</td><td>Alpha</td></tr></table>";

    private readonly SkiaHeadlessSessionFixture _fixture;

    public HtmlControlRenderScalingTests(SkiaHeadlessSessionFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(1.0, 2.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(1.0, 1.5)]
    [InlineData(1.5, 1.0)]
    public Task Block_Should_KeepItsLogicalSize_AndBeSharp_AcrossScalingChanges(double from, double to)
        => _fixture.Session.Dispatch(() =>
        {
            var (window, panel) = Show(BlockHtml, from);
            try
            {
                foreach (var scaling in new[] { from, to, from })
                {
                    window.SetRenderScaling(scaling);
                    var frame = window.CaptureRenderedFrame()!;

                    var block = DarkExtent(frame, 64);
                    Assert.InRange(block.Width, 50 * scaling - 1, 50 * scaling + 1);
                    Assert.InRange(block.Height, 30 * scaling - 1, 30 * scaling + 1);
                    // A cache rasterized for another scaling shows up resampled, with grey edges.
                    Assert.Equal(0, GreyPixelCount(frame));
                    AssertCacheMatchesScaling(panel, scaling);
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Theory]
    [InlineData(1.0, 2.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(1.0, 1.5)]
    public Task TextAndTable_Should_KeepTheirLogicalGeometry_AcrossScalingChanges(double from, double to)
        => _fixture.Session.Dispatch(() =>
        {
            var (window, panel) = Show(TextHtml, 1.0);
            try
            {
                var reference = LogicalInk(window.CaptureRenderedFrame()!, 1.0);

                foreach (var scaling in new[] { from, to, from })
                {
                    window.SetRenderScaling(scaling);
                    AssertSameGeometry(reference, LogicalInk(window.CaptureRenderedFrame()!, scaling), $"after switching to {scaling}");

                    // New output arriving on the new display — forces a fresh paint at this scaling.
                    panel.InvalidateContent();
                    AssertSameGeometry(reference, LogicalInk(window.CaptureRenderedFrame()!, scaling), $"repainted at {scaling}");
                    AssertCacheMatchesScaling(panel, scaling);
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    // Results lives in tabs that detach/re-attach HtmlPanel (OutputHtmlView); the display may change
    // while it's detached.
    [Fact]
    public Task Panel_ReattachedAfterAScalingChange_Should_RenderAtTheNewScaling()
        => _fixture.Session.Dispatch(() =>
        {
            var (window, panel) = Show(BlockHtml, 1.0);
            try
            {
                window.Content = null;
                Assert.Null(panel.CachedBitmapForTests);

                window.SetRenderScaling(2.0);
                window.Content = panel;

                var frame = window.CaptureRenderedFrame()!;
                var block = DarkExtent(frame, 64);
                Assert.InRange(block.Width, 99, 101);
                Assert.InRange(block.Height, 59, 61);
                Assert.Equal(0, GreyPixelCount(frame));
                AssertCacheMatchesScaling(panel, 2.0);

                window.SetRenderScaling(1.0);
                window.CaptureRenderedFrame();
                AssertCacheMatchesScaling(panel, 1.0);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    private static (Window Window, HtmlPanel Panel) Show(string html, double scaling)
    {
        var panel = new HtmlPanel { Background = Brushes.White, Text = html };
        var window = new Window { Width = 300, Height = 120, Background = Brushes.White, Content = panel };
        window.Show();
        window.SetRenderScaling(scaling);
        window.CaptureRenderedFrame();
        return (window, panel);
    }

    private static void AssertCacheMatchesScaling(HtmlControl control, double scaling)
    {
        var bitmap = control.CachedBitmapForTests;
        Assert.NotNull(bitmap);
        Assert.Equal((int)Math.Ceiling(control.Bounds.Width * scaling), bitmap.PixelSize.Width);
        Assert.Equal((int)Math.Ceiling(control.Bounds.Height * scaling), bitmap.PixelSize.Height);
    }

    private static void AssertSameGeometry(Rect expected, Rect actual, string because)
    {
        const double tolerance = 2;
        Assert.True(
            Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance &&
            Math.Abs(expected.Width - actual.Width) <= tolerance && Math.Abs(expected.Height - actual.Height) <= tolerance,
            $"ink {because}: expected ~{expected} DIP, got {actual} DIP");
    }

    // Bounding box of text/border ink, converted back to DIPs.
    private static Rect LogicalInk(Bitmap frame, double scaling)
    {
        var ink = DarkExtent(frame, 128);
        Assert.True(ink.Width > 0, "nothing was rendered");
        return new Rect(ink.X / scaling, ink.Y / scaling, ink.Width / scaling, ink.Height / scaling);
    }

    private static PixelRect DarkExtent(Bitmap bitmap, byte threshold)
    {
        var pixels = ReadPixels(bitmap);
        var width = bitmap.PixelSize.Width;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < threshold && pixels[i + 1] < threshold && pixels[i + 2] < threshold)
            {
                var x = i / 4 % width;
                var y = i / 4 / width;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }
        return maxX < 0 ? default : new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static int GreyPixelCount(Bitmap bitmap)
    {
        var pixels = ReadPixels(bitmap);
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 1] is > 32 and < 224)
                count++;
        }
        return count;
    }

    private static byte[] ReadPixels(Bitmap bitmap)
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
        return buffer;
    }
}
