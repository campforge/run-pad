using Avalonia;

namespace RunPad.Avalonia.HtmlRenderer;

// TODO.md #108 (was "#72" — orphaned by a later archival/renumbering pass, see #108's own note):
// decides whether HtmlControl.Render needs to actually repaint the html (expensive —
// runs the full layout engine's PerformPaint) or can just blit the offscreen bitmap it cached last
// time. Kept separate from HtmlControl/RenderTargetBitmap so the policy itself — "redraw only on
// the first paint, an explicit content invalidation, or a size change; otherwise reuse the cache" —
// is unit-testable without a live Avalonia rendering platform.
internal sealed class LazyRenderCache
{
    private Size _cachedSize;
    private bool _hasCachedSize;
    private bool _needsRedraw = true;

    public bool ShouldRedraw(Size currentSize)
        => _needsRedraw || !_hasCachedSize || _cachedSize != currentSize;

    public void MarkRedrawn(Size size)
    {
        _cachedSize = size;
        _hasCachedSize = true;
        _needsRedraw = false;
    }

    public void Invalidate() => _needsRedraw = true;
}
