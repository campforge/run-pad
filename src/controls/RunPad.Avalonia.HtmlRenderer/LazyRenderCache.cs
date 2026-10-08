using Avalonia;

namespace RunPad.Avalonia.HtmlRenderer;

// TODO.md #108 (was "#72" — orphaned by a later archival/renumbering pass, see #108's own note):
// decides whether HtmlControl.Render needs to actually repaint the html (expensive —
// runs the full layout engine's PerformPaint) or can just blit the offscreen bitmap it cached last
// time. Kept separate from HtmlControl/RenderTargetBitmap so the policy itself — "redraw only on
// the first paint, an explicit content invalidation, a size change or a render scaling change;
// otherwise reuse the cache" — is unit-testable without a live Avalonia rendering platform.
// Render scaling is part of the key because the cached bitmap is rasterized at device pixel
// density: moving the window to a display with a different scaling (e.g. external monitor ->
// MacBook Retina) keeps the same logical size but needs a bitmap with a different pixel size.
internal sealed class LazyRenderCache
{
    private Size _cachedSize;
    private double _cachedScaling;
    private bool _hasCachedSize;
    private bool _needsRedraw = true;

    public bool ShouldRedraw(Size currentSize, double currentScaling)
        => _needsRedraw || !_hasCachedSize || _cachedSize != currentSize || _cachedScaling != currentScaling;

    public void MarkRedrawn(Size size, double scaling)
    {
        _cachedSize = size;
        _cachedScaling = scaling;
        _hasCachedSize = true;
        _needsRedraw = false;
    }

    public void Invalidate() => _needsRedraw = true;
}
