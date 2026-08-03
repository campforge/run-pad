using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RunPad.Avalonia.HtmlRenderer;
using RunPad.Avalonia.HtmlRenderer.Adapters;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #108: live user report — text selection dragged with the mouse over .Dump() output
// appeared to lag/not visually update ("looks selected but isn't highlighted as
// selected"). Root cause: HtmlControl.Render blits an offscreen cache (LazyRenderCache, TODO.md
// #72) unless told the content is actually stale via InvalidateContent() — but SelectionHandler
// (External/Core/Handlers/SelectionHandler.cs) drives every mid-drag selection-change repaint through
// RControl.Invalidate() -> ControlAdapter.Invalidate(), which used to call a bare
// Control.InvalidateVisual() and never touched the cache at all.
//
// [AvaloniaFact] (not a plain [Fact]) because constructing an HtmlControl needs a live Avalonia
// platform — AvaloniaAdapter's static ctor resolves FontManager.Current, which throws without one;
// RunPad.Avalonia.HtmlRenderer.Tests deliberately has no Avalonia.Headless dependency (its own tests
// are pure-logic), so these live here instead, granted access via a second
// [assembly: InternalsVisibleTo] on RunPad.Avalonia.HtmlRenderer (see its AssemblyInfo.cs).
public class HtmlControlInvalidationTests
{
    [AvaloniaFact]
    public void ControlAdapter_Invalidate_Should_MarkTheRenderCacheStale_ForAnHtmlControl()
    {
        var htmlControl = new HtmlPanel();
        var adapter = new AvaloniaAdapter(htmlControl);
        var controlAdapter = new ControlAdapter(adapter, htmlControl);

        var size = new Size(100, 100);
        htmlControl.RenderCacheForTests.MarkRedrawn(size);
        Assert.False(htmlControl.RenderCacheForTests.ShouldRedraw(size)); // cache considered fresh

        controlAdapter.Invalidate();

        Assert.True(htmlControl.RenderCacheForTests.ShouldRedraw(size)); // now correctly stale
    }

    // Defensive fallback: ControlAdapter can in principle wrap any Avalonia Control (its field is
    // typed as the base Control, not HtmlControl specifically) — for anything that isn't an
    // HtmlControl there's no render cache to speak of, so this must still fall back to a plain
    // InvalidateVisual() rather than throwing.
    [AvaloniaFact]
    public void ControlAdapter_Invalidate_Should_NotThrow_ForANonHtmlControl()
    {
        var plainControl = new Border();
        var adapter = new AvaloniaAdapter(plainControl);
        var controlAdapter = new ControlAdapter(adapter, plainControl);

        controlAdapter.Invalidate();
    }

    // Second half of the same live report: Ctrl+A/Ctrl+C "reached but unreachable" — HtmlPanel.
    // OnPointerReleased already calls Focus(), and HtmlControl's KeyDown handler already forwards to
    // HtmlContainerInt.HandleKeyDown (which already handles Ctrl+A/Ctrl+C) — but neither HtmlControl
    // nor HtmlPanel ever set Focusable, so Focus() was silently a no-op and KeyDown could never fire.
    [AvaloniaFact]
    public void HtmlControl_Should_BeFocusable_SoKeyboardCopySelectAllCanReachIt()
    {
        var panel = new HtmlPanel();
        Assert.True(panel.Focusable);

        var label = new HtmlLabel();
        Assert.True(label.Focusable);
    }
}
