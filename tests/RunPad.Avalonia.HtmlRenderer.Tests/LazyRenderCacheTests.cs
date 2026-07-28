using Avalonia;
using Xunit;

namespace RunPad.Avalonia.HtmlRenderer.Tests;

// TODO.md #72: HtmlControl.Render used to call _htmlContainer.PerformPaint unconditionally on
// every Render call, even innocent invalidations that don't change what's on screen (window resize
// of unrelated controls, sibling repaints). LazyRenderCache holds the policy for when a real
// repaint is actually needed vs. when Render can just blit the cached bitmap — tested directly here
// since exercising it through HtmlControl itself would need a live Avalonia rendering platform.
public class LazyRenderCacheTests
{
    [Fact]
    public void ShouldRedraw_Should_BeTrue_OnFirstCall()
    {
        var cache = new LazyRenderCache();

        Assert.True(cache.ShouldRedraw(new Size(100, 100)));
    }

    [Fact]
    public void ShouldRedraw_Should_BeFalse_AfterMarkRedrawn_WithTheSameSize()
    {
        var cache = new LazyRenderCache();
        var size = new Size(100, 100);

        cache.MarkRedrawn(size);

        Assert.False(cache.ShouldRedraw(size));
    }

    [Fact]
    public void ShouldRedraw_Should_BeTrue_WhenSizeChanges()
    {
        var cache = new LazyRenderCache();
        cache.MarkRedrawn(new Size(100, 100));

        Assert.True(cache.ShouldRedraw(new Size(200, 100)));
    }

    [Fact]
    public void ShouldRedraw_Should_BeTrue_AfterInvalidate_EvenWithTheSameSize()
    {
        var cache = new LazyRenderCache();
        var size = new Size(100, 100);
        cache.MarkRedrawn(size);

        cache.Invalidate();

        Assert.True(cache.ShouldRedraw(size));
    }

    [Fact]
    public void MarkRedrawn_Should_ClearAPriorInvalidate()
    {
        var cache = new LazyRenderCache();
        var size = new Size(100, 100);
        cache.Invalidate();

        cache.MarkRedrawn(size);

        Assert.False(cache.ShouldRedraw(size));
    }
}
