using Avalonia;
using Xunit;

namespace Invoklet.Avalonia.HtmlRenderer.Tests;

// HtmlControl.Render used to call _htmlContainer.PerformPaint unconditionally on
// every Render call, even innocent invalidations that don't change what's on screen (window resize
// of unrelated controls, sibling repaints). LazyRenderCache holds the policy for when a real
// repaint is actually needed vs. when Render can just blit the cached bitmap — tested directly here;
// the rendered-pixel consequences of the policy are covered by HtmlControlRenderScalingTests.
public class LazyRenderCacheTests
{
    [Fact]
    public void ShouldRedraw_Should_BeTrue_OnFirstCall()
    {
        var cache = new LazyRenderCache();

        Assert.True(cache.ShouldRedraw(new Size(100, 100), 1.0));
    }

    [Fact]
    public void ShouldRedraw_Should_BeFalse_AfterMarkRedrawn_WithTheSameSizeAndScaling()
    {
        var cache = new LazyRenderCache();
        var size = new Size(100, 100);

        cache.MarkRedrawn(size, 2.0);

        Assert.False(cache.ShouldRedraw(size, 2.0));
    }

    [Fact]
    public void ShouldRedraw_Should_BeTrue_WhenSizeChanges()
    {
        var cache = new LazyRenderCache();
        cache.MarkRedrawn(new Size(100, 100), 1.0);

        Assert.True(cache.ShouldRedraw(new Size(200, 100), 1.0));
    }

    // Window moved between displays: same logical bounds, different pixel density.
    [Theory]
    [InlineData(1.0, 2.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(1.0, 1.5)]
    public void ShouldRedraw_Should_BeTrue_WhenScalingChanges_AtTheSameSize(double from, double to)
    {
        var cache = new LazyRenderCache();
        var size = new Size(100, 100);
        cache.MarkRedrawn(size, from);

        Assert.True(cache.ShouldRedraw(size, to));
    }

    [Fact]
    public void ShouldRedraw_Should_BeTrue_AfterInvalidate_EvenWithTheSameSizeAndScaling()
    {
        var cache = new LazyRenderCache();
        var size = new Size(100, 100);
        cache.MarkRedrawn(size, 1.0);

        cache.Invalidate();

        Assert.True(cache.ShouldRedraw(size, 1.0));
    }

    [Fact]
    public void MarkRedrawn_Should_ClearAPriorInvalidate()
    {
        var cache = new LazyRenderCache();
        var size = new Size(100, 100);
        cache.Invalidate();

        cache.MarkRedrawn(size, 1.0);

        Assert.False(cache.ShouldRedraw(size, 1.0));
    }
}
