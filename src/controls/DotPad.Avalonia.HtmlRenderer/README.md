# DotPad.Avalonia.HtmlRenderer

Avalonia port of the [HTML Renderer](https://github.com/ArthurHub/HTML-Renderer) library (originally by Arthur Teplitzki / TheArtOfDev). Provides a custom HTML/CSS rendering engine for Avalonia controls — no WebView, no Chromium, no native OS web component. All rendering is done on Avalonia's `DrawingContext`.

---

## Table of Contents

- [Overview](#overview)
- [Quick Start](#quick-start)
- [Public API](#public-api)
  - [HtmlPanel](#htmlpanel)
  - [HtmlLabel](#htmllabel)
  - [HtmlControl (base class)](#htmlcontrol-base-class)
  - [HtmlContainer](#htmlcontainer)
  - [HtmlRender (static)](#htmlrender-static)
- [Rendering Pipeline](#rendering-pipeline)
- [CSS Support](#css-support)
  - [Supported Properties](#supported-properties)
  - [Supported Shorthands](#supported-shorthands)
  - [Unsupported / Silently Ignored](#unsupported--silently-ignored)
- [Font Handling](#font-handling)
- [Architecture: Adapter Pattern](#architecture-adapter-pattern)
- [Known Issues and Pitfalls](#known-issues-and-pitfalls)
- [Improvement Opportunities](#improvement-opportunities)

---

## Overview

`DotPad.Avalonia.HtmlRenderer` is a fully managed, dependency-light HTML rendering library. It parses HTML and CSS, performs layout, and draws the result using Avalonia's vector graphics API.

**Use cases:**
- Rich-text output panels (styled text, tables, syntax highlighting)
- Tooltips with HTML content
- Embedding formatted reports or documentation fragments
- Rendering HTML to `Bitmap` without a UI (off-screen)

**Not a browser.** JavaScript, iframes, and modern CSS features (Grid, Flexbox, animations, variables) are not supported. See [CSS Support](#css-support) for details.

---

## Quick Start

### AXAML

```xml
xmlns:html="clr-namespace:DotPad.Avalonia.HtmlRenderer;assembly=DotPad.Avalonia.HtmlRenderer"

<!-- Scrollable panel — use when the container has a fixed size -->
<html:HtmlPanel x:Name="OutputPanel"
                Background="{DynamicResource MyBackgroundBrush}" />

<!-- Auto-sizing label — use when the control should shrink-wrap its content -->
<html:HtmlLabel x:Name="TooltipLabel" AutoSize="True" />
```

### Code-behind

```csharp
// Set HTML content (full document or fragment)
OutputPanel.Text = """
    <html>
    <head><style>
        body { font-family: Consolas, monospace; font-size: 13px; }
        .error { color: #CC0000; }
    </style></head>
    <body>
        <p>Hello <b>World</b></p>
        <p class="error">Something went wrong.</p>
    </body>
    </html>
    """;

// Register a custom font (call once, before setting Text)
OutputPanel.Container.AddFontFamily(
    new FontFamily(new Uri("avares://MyApp/Assets/fonts/MyFont.ttf"), "My Font"));

// Scroll to an anchor element by id
OutputPanel.ScrollToElement("section-3");

// Handle link clicks
OutputPanel.LinkClicked += (s, e) =>
{
    e.Event.Handled = true; // prevent default navigation
    OpenUrl(e.Event.Link);
};
```

### Off-screen rendering to image

```csharp
var bitmap = HtmlRender.RenderToImage(html, new Size(800, 600));
bitmap.Save("output.png");
```

---

## Public API

### HtmlPanel

```
HtmlPanel : HtmlControl
```

Scrollable HTML panel. Shows horizontal and vertical scrollbars when content exceeds the control bounds. The scroll position resets to zero when `Text` changes.

| Member | Description |
|--------|-------------|
| `ScrollToElement(string elementId)` | Scrolls the panel so the top of the element with the given `id` attribute is at the top of the view. |
| Keyboard: `↑ ↓ PgUp PgDn Home End` | Scroll vertically. |
| Keyboard: `← →` | Scroll horizontally. |
| Mouse wheel | Scrolls vertically by 20px per tick. |

**When to use:** The output panel has a fixed size driven by layout. Excess content scrolls.

### HtmlLabel

```
HtmlLabel : HtmlControl
```

Auto-sizing HTML control with no scrollbars. Content outside the bounds is clipped.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `AutoSize` | `bool` | `true` | Sizes width and height to fit the content. Mutually exclusive with `AutoSizeHeightOnly`. |
| `AutoSizeHeightOnly` | `bool` | `false` | Sizes only the height; width stays as given by the layout. Mutually exclusive with `AutoSize`. |

**When to use:** Tooltips, inline badges, any case where the control must be as large as its content.

### HtmlControl (base class)

All Avalonia controls in this library inherit from `HtmlControl`, which provides the full common API.

#### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Text` | `string` | `null` | The HTML source to render. Accepts full documents (`<html>...</html>`) or bare fragments (`<p>...</p>`). Setting this resets the scroll position and triggers a full re-layout. |
| `BaseStylesheet` | `string` | `null` | Additional CSS applied before `<style>` blocks in `Text`. Use to inject a theme stylesheet without embedding it in the HTML. |
| `Background` | `IBrush` | Transparent | Background fill drawn before HTML content. |
| `BorderThickness` | `Thickness` | 0 | Border thickness. Border is always rendered in black (see [Known Issues](#known-issues-and-pitfalls)). |
| `Padding` | `Thickness` | 0 | Inner padding between the control edge and HTML content. |
| `IsSelectionEnabled` | `bool` | `true` | Enables mouse text selection and `Ctrl+C` copy. |
| `IsContextMenuEnabled` | `bool` | `true` | Shows a built-in right-click context menu (Copy, Select All). |
| `AvoidImagesLateLoading` | `bool` | `false` | When `true`, images are loaded synchronously during parse. When `false`, images outside the scroll viewport are deferred. |
| `SelectedText` | `string` | — | Currently selected plain text (read-only). |
| `SelectedHtml` | `string` | — | Currently selected HTML with inline styles (read-only). |
| `Container` | `HtmlContainer` | — | Access to the low-level container (font registration, DOM queries). |

#### Events

All events bubble up through the Avalonia routing system as `HtmlRendererRoutedEventArgs<T>`. The inner `T` is accessible via `.Event`.

| Event | Arg type | Description |
|-------|----------|-------------|
| `LoadComplete` | `EventArgs` | Fired after the HTML is fully parsed and the initial layout is done. |
| `LinkClicked` | `HtmlLinkClickedEventArgs` | Fired when the user clicks an `<a>` element. Set `e.Event.Handled = true` to suppress the default link navigation. |
| `RenderError` | `HtmlRenderErrorEventArgs` | Fired when a non-fatal rendering error occurs (e.g., unresolvable image). |
| `StylesheetLoad` | `HtmlStylesheetLoadEventArgs` | Fired when a `<link rel="stylesheet">` is encountered. Allows providing CSS data manually. |
| `ImageLoad` | `HtmlImageLoadEventArgs` | Fired when an `<img>` src is about to be loaded. Allows providing the image data manually. |

#### Methods

| Method | Description |
|--------|-------------|
| `GetHtml()` | Serializes the current DOM back to HTML with inline styles. |
| `GetElementRectangle(string id)` | Returns the layout rectangle of an element by its `id`, or `null` if not found. |
| `ClearSelection()` | Clears the current text selection. |

### HtmlContainer

`HtmlContainer` is the low-level object that owns the DOM and drives layout and paint. It is exposed via `HtmlControl.Container` and can also be instantiated directly for off-screen rendering.

```csharp
using var container = new HtmlContainer(hostControl); // hostControl can be null for off-screen use
container.SetHtml(html, cssData);
container.PerformLayout();
container.PerformPaint(drawingContext, clipRect);
```

Additional capabilities not available on `HtmlControl`:

| Member | Description |
|--------|-------------|
| `AddFontFamily(FontFamily)` | Registers a custom font family for use in HTML rendering. The font object must stay alive for the duration of rendering. |
| `AddFontFamilyMapping(string from, string to)` | Substitutes a font family name when the original is not found (e.g., map `"monospace"` → `"Consolas"`). |
| `GetLinks()` | Returns all `<a>` links with their layout rectangles. |
| `GetAttributeAt(Point, string attr)` | Returns the value of an HTML attribute at a given pixel coordinate. |
| `GetLinkAt(Point)` | Returns the `href` of the link at a given coordinate, or `null`. |
| `MaxSize` | Sets the maximum layout width/height. Width wraps lines; height clips (does not affect layout). |
| `ScrollOffset` | Gets or sets the scroll offset for the rendered content. |
| `ActualSize` | The size of the rendered content after layout (can exceed `MaxSize` if content cannot be wrapped). |
| `AvoidAsyncImagesLoading` | Forces synchronous image loading — required for off-screen / image export scenarios. |
| `IsSelectionEnabled` | Controls whether text selection is active. |
| `IsContextMenuEnabled` | Controls whether the built-in right-click menu appears. |

### HtmlRender (static)

`HtmlRender` is a static helper for one-shot rendering without a persistent control.

```csharp
// Measure size required to render HTML
Size size = HtmlRender.Measure(html, maxWidth: 800);

// Render to an existing DrawingContext
HtmlRender.Render(drawingContext, html, left: 0, top: 0, maxWidth: 800);

// Render to a Bitmap of a fixed size
Bitmap bmp = HtmlRender.RenderToImage(html, new Size(800, 600));

// Render to a Bitmap, auto-sizing to content
Bitmap bmp = HtmlRender.RenderToImage(html, maxWidth: 800);

// Parse a standalone stylesheet (to pass to SetHtml or Measure)
CssData css = HtmlRender.ParseStyleSheet("body { font-size: 14px; }");
```

---

## Rendering Pipeline

The full pipeline from HTML string to pixels:

```
Text property set
        │
        ▼
HtmlControl.OnPropertyChanged
        │
        ▼
HtmlContainer.SetHtml(html, cssData)
        │
        ├─ CSS parse: CssParser → CssData (stylesheet cascade)
        └─ DOM parse: DomParser → CssBox tree
                │
                ▼
      Avalonia MeasureOverride
                │
                ▼
      HtmlContainer.PerformLayout()
                │
                ▼
      CssLayoutEngine.PerformLayout()   ← measures & positions each CssBox
                │
                ▼
      HtmlControl.Render(DrawingContext)
                │
                ▼
      HtmlContainer.PerformPaint(DrawingContext, Rect)
                │
                ▼
      HtmlContainerInt.PerformPaint(RGraphics)
                │
                ▼
      Each CssBox.Paint(RGraphics)
                │
          ┌─────┴───────────────────────┐
          │                             │
  BordersDrawHandler          BackgroundImageDrawHandler
  (borders, rounded corners)   (background fills)
          │                             │
          └─────────┬───────────────────┘
                    ▼
          GraphicsAdapter (Avalonia-specific)
                    │
                    ▼
          Avalonia DrawingContext
          (DrawLine, FillRectangle, DrawText, DrawGlyphRun, ...)
```

### Key stages

**1. CSS Parsing** (`CssParser`, `CssValueParser`)  
CSS rules are parsed into a `CssData` object (map of selector → property dictionary). The W3C default browser stylesheet is merged in as the base.

**2. DOM Parsing** (`DomParser`, `HtmlParser`)  
HTML is parsed into a tree of `CssBox` objects. Each box stores its resolved CSS property values as strings. HTML attributes (`bgcolor`, `border`, `cellpadding`, etc.) are translated to equivalent CSS.

**3. Layout** (`CssLayoutEngine`, `CssLayoutEngineTable`)  
A single-pass layout engine computes the position and size of every box. Handles block, inline, table, and list display modes. Does not support Flexbox or Grid.

**4. Paint**  
Each box renders its background, then borders, then children, then text. Text is rendered via Avalonia's `TextFormatter` API (`DrawGlyphRun`) for high-quality shaping.

---

## CSS Support

### Supported Properties

| Category | Properties |
|----------|-----------|
| **Display / Layout** | `display` (block, inline, table, table-row, table-cell, list-item, none), `position` (static, relative, absolute — limited), `float`, `overflow`, `visibility` |
| **Box model** | `width`, `height`, `max-width`, `margin-*`, `padding-*`, `left`, `top` |
| **Borders** | `border-*-width`, `border-*-style`, `border-*-color`, `border-collapse`, `border-spacing` |
| **Background** | `background-color`, `background-image`, `background-position`, `background-repeat` |
| **Color** | `color` |
| **Typography** | `font-family`, `font-size`, `font-style`, `font-weight`, `font-variant`, `line-height`, `text-align`, `text-decoration`, `text-indent`, `vertical-align`, `white-space`, `word-break`, `word-spacing` |
| **Lists** | `list-style`, `list-style-type`, `list-style-position`, `list-style-image` |
| **Table** | `empty-cells`, `page-break-inside` |
| **Direction** | `direction` (ltr / rtl) |
| **Extras (non-standard)** | `background-gradient`, `background-gradient-angle` — proprietary gradient support |

### Supported Shorthands

| Shorthand | Expands to |
|-----------|-----------|
| `border` | `border-*-width`, `border-*-style`, `border-*-color` |
| `border-top / right / bottom / left` | individual side properties |
| `border-color` | all four side colors |
| `margin` | `margin-top/right/bottom/left` |
| `padding` | `padding-top/right/bottom/left` |
| `font` | `font-style`, `font-weight`, `font-size`, `font-family` |

### Unsupported / Silently Ignored

> These properties are parsed by the CSS tokenizer but have no effect. No error is raised.

| Property / Feature | Notes |
|-------------------|-------|
| **`background`** shorthand | **Critical.** Only `background-color`, `background-image`, etc. work. `background: #color` is silently ignored. Always write `background-color: #color`. |
| `border-radius` | Only non-standard `corner-nw-radius`, `corner-ne-radius`, `corner-se-radius`, `corner-sw-radius` are supported internally. |
| `opacity` | Not supported. |
| `transform` | Not supported. |
| `filter` | Not supported. |
| `box-shadow` | Not supported. |
| `transition` / `animation` | Not supported. Static rendering only. |
| `CSS Grid` / `Flexbox` | Not supported. Use `table` layout for multi-column arrangements. |
| `@media` queries | Not supported. |
| `var()` / CSS custom properties | Not supported. |
| `:hover`, `:focus`, `:active` | Not supported. |
| `calc()` | Not supported. |
| `clip-path` | Not supported. |

---

## Font Handling

At startup, `AvaloniaAdapter` registers all system fonts from `FontManager.Current.SystemFonts`. Two default mappings are pre-configured:

| CSS font | Resolved to |
|----------|------------|
| `monospace` | `Courier New` |
| `Helvetica` | `Arial` |

### Registering a custom font

Custom fonts (e.g., embedded in `avares://`) must be registered before the HTML is set:

```csharp
panel.Container.AddFontFamily(
    new FontFamily(new Uri("avares://MyApp/Assets/fonts/JetBrainsMono.ttf"), "JetBrains Mono"));
```

> The `FontFamily` object must remain in memory for the lifetime of the renderer.

### Adding a font fallback mapping

```csharp
panel.Container.AddFontFamilyMapping("SomeMissingFont", "Segoe UI");
```

If `SomeMissingFont` is not found in the font registry, `Segoe UI` is used instead.

---

## Architecture: Adapter Pattern

The library is split into three layers:

```
┌─────────────────────────────────────────┐
│         Public API (this project)        │
│  HtmlPanel / HtmlLabel / HtmlRender      │
│  HtmlControl / HtmlContainer             │
└───────────────┬─────────────────────────┘
                │
┌───────────────▼─────────────────────────┐
│    Avalonia Adapters (Adapters/)         │
│  AvaloniaAdapter   → platform services  │
│  GraphicsAdapter   → DrawingContext      │
│  FontAdapter       → Typeface / TextRun  │
│  BrushAdapter      → IBrush             │
│  PenAdapter        → IPen               │
│  ImageAdapter      → Bitmap             │
│  ContextMenuAdapter → ContextMenu        │
└───────────────┬─────────────────────────┘
                │
┌───────────────▼─────────────────────────┐
│    Core Engine (External/Core/)          │
│  Platform-agnostic HTML/CSS engine       │
│  (ported from TheArtOfDev.HtmlRenderer)  │
│                                          │
│  HtmlContainerInt   — orchestrator       │
│  DomParser          — HTML → CssBox tree │
│  CssParser          — CSS → CssData      │
│  CssLayoutEngine    — box layout         │
│  CssLayoutEngineTable — table layout     │
│  SelectionHandler   — text selection     │
│  ImageLoadHandler   — async image load   │
└─────────────────────────────────────────┘
```

**Abstract adapter interfaces** live in `External/Adapters/` (e.g., `RAdapter`, `RGraphics`, `RFont`, `RBrush`). The Avalonia-specific implementations live in `Adapters/`. To port the renderer to a different graphics API, only the `Adapters/` layer needs to be replaced.

---

## Known Issues and Pitfalls

### 1. `background` shorthand is silently ignored

```css
/* ❌ does nothing — shorthand not parsed */
.my-class { background: #1E1E1E; }

/* ✅ works correctly */
.my-class { background-color: #1E1E1E; }
```

This is the single most common mistake. The shorthand is valid CSS but `CssParser` has no handler for it — it falls through without setting any property.

### 2. `BorderBrush` setter has a typo

In `HtmlControl.cs`, the `BorderBrush` property setter mistakenly calls `SetValue(BorderThicknessProperty, value)` instead of `SetValue(BorderBrushProperty, value)`. As a result, setting `BorderBrush` via code overwrites `BorderThickness` instead. The AXAML binding form works correctly.

Additionally, the border is always drawn in black regardless of `BorderBrush` (`ImmutableSolidColorBrush(Colors.Black)` is hardcoded in `Render()`).

### 3. Thread safety: `.Wait()` on internal event dispatch

When an `HtmlContainerInt` event (e.g., `Refresh`, `RenderError`) fires on a background thread, `HtmlControl` marshals it to the UI thread via:

```csharp
Dispatcher.UIThread.InvokeAsync(() => OnRefresh(e)).Wait();
```

The `.Wait()` blocks the calling thread until the UI processes the event. Under heavy load or if the UI thread is already busy, this can cause brief freezes. It also prevents `async` handlers from working correctly.

### 4. Image clipboard copy not implemented

`AvaloniaAdapter.SetToClipboardInt(RImage)` is a no-op (silently skipped). Copying an image to the clipboard from the context menu does nothing.

### 5. Anti-aliasing is a no-op

`GraphicsAdapter.SetAntiAliasSmoothingMode()` returns `null` — the Avalonia `DrawingContext` does not expose a global smoothing mode switch, so text relies on Avalonia's default rendering quality.

### 6. `border-radius` uses non-standard properties

The engine has internal support for rounded corners via `corner-nw-radius`, `corner-ne-radius`, `corner-se-radius`, `corner-sw-radius`. The standard `border-radius` shorthand is not mapped to these properties and is silently ignored.

### 7. Full re-layout on every `Text` change

Setting `Text` always triggers a full CSS re-parse, DOM rebuild, and layout pass. There is no incremental update or diffing. For output panels that append content frequently (e.g., streaming script output), concatenate HTML server-side and set `Text` once, or throttle updates.

---

## Improvement Opportunities

| Priority | Improvement | Notes |
|----------|-------------|-------|
| 🔴 High | **Implement `background` shorthand** | Parse `background: color [image] [position] [repeat]` in `CssParser` and expand to individual properties. Most visual bugs stem from this. |
| 🔴 High | **Fix `BorderBrush` setter** | Change `SetValue(BorderThicknessProperty, value)` to `SetValue(BorderBrushProperty, value)` and use the brush in `Render()`. |
| 🟠 Medium | **Standard `border-radius`** | Map `border-radius` to `corner-*-radius` properties in `CssBoxProperties`. |
| 🟠 Medium | **Replace `.Wait()` with proper async dispatch** | Use `await Dispatcher.UIThread.InvokeAsync(...)` instead of `.Wait()` in event handlers to eliminate blocking. |
| 🟠 Medium | **Image clipboard support** | Implement `SetToClipboardInt(RImage)` using `TopLevel.Clipboard` and Avalonia's `DataObject` API. |
| 🟡 Low | **`rgba()` / `hsla()` color validation** | `AvaloniaAdapter.GetColorInt()` validates names against `typeof(Colors)` properties, which excludes functional colors. Functional color parsing is handled earlier in `CssValueParser`, but coverage should be verified. |
| 🟡 Low | **Incremental layout** | Cache the parsed CSS and DOM, re-layout only on size change rather than on every `Text` set. |
| 🟡 Low | **`opacity` property** | Map `opacity` to `DrawingContext.PushOpacity()` push/pop around box rendering. |
| 🟡 Low | **`@media` queries** | Useful for print-style stylesheets in export scenarios. |
| 🟡 Low | **`text-overflow: ellipsis`** | Currently unsupported; text wraps or is clipped without truncation indicator. |
| 🟡 Low | **SVG images** | Avalonia supports SVG via the `Avalonia.Svg` package; wiring it into `ImageAdapter` would enable inline SVG via `<img>`. |

---

## Future Features: Large Document & Streaming Output Support

> This section is a detailed technical analysis of two related performance features:
> **viewport culling** (render only the visible area) and **incremental DOM append** (add elements without re-parsing the whole document).
> Both are relevant for long-running script output panels that accumulate thousands of output lines.

---

### 1. Current Bottlenecks (Identified from Source)

#### 1.1 Paint traversal visits all boxes — always

`HtmlContainerInt.PerformPaint()` calls `_root.Paint(g)`, which recursively calls `Paint(g)` on every `CssBox` in the tree.

`CssBox.Paint()` ([CssBox.cs:476](External/Core/Dom/CssBox.cs)) does have a visibility check:

```csharp
bool visible = Rectangles.Count == 0;
if (!visible)
{
    var clip = g.GetClip();                          // viewport rect
    var rect = ContainingBlock.ClientRectangle;      // ← parent's rect, NOT this box's rect
    rect.Offset(HtmlContainer.ScrollOffset);
    clip.Intersect(rect);
    if (clip != RRect.Empty) visible = true;
}
if (visible) PaintImp(g);
```

**The bug:** visibility is checked against `ContainingBlock.ClientRectangle` — the parent block's bounding rectangle — not the box's own rectangle. Since `<body>` is the containing block of every top-level `<div>`, and body covers the entire document height, the intersection with the viewport is always non-empty. **Every `<div>` is painted on every frame**, even if fully scrolled off screen.

With 1000 output lines, each containing a table:
- 1000 `<div>` Paint calls
- 1000 × N table cell Paint calls
- 1000 × M text word Paint calls

Even though Avalonia's GPU clipping prevents incorrect pixels, the CPU traversal is O(total elements), not O(visible elements).

#### 1.2 Every Text change re-parses everything

Setting `HtmlControl.Text` triggers:

```
SetHtml()
  → HtmlParser  (re-tokenize full HTML)
  → DomParser   (rebuild full CssBox tree)
  → CssParser   (re-parse inline styles)
  → PerformLayout() on ALL boxes
  → InvalidateVisual()
```

For a streaming output panel receiving 100 lines/second, each append is O(total accumulated lines), making the total cost O(n²).

#### 1.3 Layout is always full-pass

`CssLayoutEngine.PerformLayout()` starts from Y=0 and processes every box sequentially. There is no concept of "layout from box N onward" or "only boxes that changed". Even if one `<div>` is appended at the bottom, the engine re-positions everything from the top.

---

### 2. Feature A — Fix Viewport Culling (Quick Win)

**Effort:** ~1 day | **Risk:** Low | **Impact:** High for large documents

#### What to change

In `CssBox.Paint()` ([CssBox.cs:492](External/Core/Dom/CssBox.cs)), replace the containing block rectangle with the box's **own** actual rectangle:

```csharp
// CURRENT — always resolves to body's full rectangle → never skips
var rect = ContainingBlock.ClientRectangle;

// FIX — use the box's actual positioned rectangle
var rect = Rectangles.Count > 0
    ? Rectangles[0]                    // first rect = box's own bounding box
    : ContainingBlock.ClientRectangle; // fallback for inline-only boxes
```

`Rectangles[0]` is the box's own layout rectangle, set during `PerformLayout()`. After adjusting for scroll offset, intersecting it with the clip rect gives the correct visibility answer.

#### Expected result

- Each `<div>` (direct child of `<body>`) is skipped if its Y range is outside the viewport.
- With 1000 lines and 30 visible: paint visits ~30-50 boxes instead of 1000.
- Layout is still O(n) — all positions are still computed. Only paint is culled.
- No change to layout correctness, text selection, link detection, or scrollbar behavior.

#### Caveat

Boxes with `Rectangles.Count == 0` are text-only boxes that belong to inline content. The fallback to `ContainingBlock.ClientRectangle` preserves current behavior for these, so inline text is unaffected.

---

### 3. Feature B — Incremental DOM Append

**Effort:** 1–2 weeks | **Risk:** Medium | **Impact:** Eliminates O(n²) cost for streaming panels

#### Goal

Add an `AppendHtml(string fragment)` API that:
1. Parses only the new HTML fragment (not the full document)
2. Appends the new `CssBox` subtrees to the existing DOM
3. Layouts only the new boxes, starting where previous content ended
4. Updates `ActualSize` and triggers a minimal repaint

#### Why it's feasible for DotPad's use case

DotPad's output is composed exclusively of **block-level elements** (`<div>`, `<pre>`, `<table>`). Each is on its own line with no inline content flowing from the previous block. This means:

- Box N+1 starts exactly at `Y = box[N].ActualBottom + marginBottom`
- Box N+1's layout does not depend on the *internal* state of any previous box
- Appending is pure vertical stacking

This is the easiest case for incremental layout.

#### Implementation Plan

**Step 1 — Track the last layout position in `HtmlContainerInt`**

```csharp
// new field
private double _appendY = 0;  // Y where next appended content should start

// update in PerformLayout() after full layout
_appendY = _root.Boxes.Count > 0
    ? _root.Boxes[^1].ActualBottom
    : _location.Y;
```

**Step 2 — Add `AppendHtmlFragment()` to `HtmlContainerInt`**

```csharp
public void AppendHtmlFragment(string htmlFragment, RGraphics g)
{
    // 1. Parse only the fragment, reuse the existing CssData
    var tempRoot = DomParser.GenerateCssTree(htmlFragment, this, ref _cssData);

    // 2. Pull the new top-level boxes out of the temp root
    var newBoxes = tempRoot.Boxes.ToList();

    // 3. Re-parent them to the real root
    foreach (var box in newBoxes)
    {
        box.SetParent(_root);
        _root.Boxes.Add(box);
    }

    // 4. Layout only the new boxes starting at _appendY
    double currentY = _appendY;
    foreach (var box in newBoxes)
    {
        box.Location = new RPoint(_location.X, currentY);
        box.Size = new RSize(_maxSize.Width > 0 ? _maxSize.Width : 99999, 0);
        box.PerformLayout(g);
        currentY = box.ActualBottom + box.ActualMarginBottom;
    }

    // 5. Update state
    _appendY = currentY;
    _actualSize = new RSize(
        Math.Max(_actualSize.Width, newBoxes.Max(b => b.ActualRight)),
        currentY - _location.Y);
}
```

**Step 3 — Expose on `HtmlContainer`**

```csharp
// HtmlContainer.cs
public void AppendHtml(string htmlFragment)
{
    using var ig = new GraphicsAdapter(_avaloniaAdapter);
    _htmlContainerInt.AppendHtmlFragment(htmlFragment, ig);
}
```

**Step 4 — Expose on `HtmlControl`**

```csharp
// HtmlControl.cs
public void AppendHtml(string htmlFragment)
{
    _htmlContainer.AppendHtml(htmlFragment);
    InvalidateMeasure();
    InvalidateVisual();
}
```

**Step 5 — Use from DotPad**

```csharp
// EditorTabItemControl.cs — instead of rebuilding OutputContent
private void AppendOutput(string data, OutputKind kind)
{
    var html = kind switch
    {
        OutputKind.DumpHtml => WrapDump(data),
        OutputKind.StandardError => $"<div class=\"error\">{HttpUtility.HtmlEncode(data)}</div>",
        _ => $"<div>{HttpUtility.HtmlEncode(data)}</div>"
    };
    OutputHtml.AppendHtml(html);
    // auto-scroll to bottom if user hasn't scrolled up
    if (!_userScrolledUp)
        OutputHtml.ScrollToEnd();
}
```

#### Edge cases and limitations

| Case | Handling |
|------|----------|
| Theme change | Must still call full `SetHtml()` — re-parses with new CSS. `AppendHtml` is append-only. |
| `Clear()` before new run | Resets to `SetHtml("")`, which resets `_appendY = 0`. |
| `<style>` tags in fragment | Should be parsed and merged into `_cssData`, or stripped (simpler). For DotPad, the stylesheet is always in the initial `<head>`, so stripping style tags from fragments is correct. |
| Window resize | `PerformLayout()` runs on all boxes from root. No change to current behavior. After resize, `_appendY` is recalculated from the last box's `ActualBottom`. |
| Text selection across appended content | Works naturally since all boxes share the same root `SelectionHandler`. |
| Tables in fragments | `CssLayoutEngineTable` handles them as opaque blocks. As long as tables are block-level, they start at a new Y and integrate correctly. |

---

### 4. Feature C — Virtual Document Architecture (Maximum Scale)

**Effort:** 3–5 weeks | **Risk:** Higher | **Impact:** Unlimited scalability, enables 100k+ output lines

#### When Features A and B are not enough

Feature A (paint culling) helps per-frame rendering but layout is still O(n) on every window resize.  
Feature B (incremental append) eliminates O(n²) cost but layout is still O(n) for resize.  

For truly huge outputs (100k+ lines, megabytes of HTML), even a single O(n) layout pass takes seconds. The solution is to never lay out off-screen content at all.

#### Core idea

Replace the single unified `HtmlContainer` with an ordered list of independent items, each with their own container. Only items in the viewport ± a buffer are actively laid out and rendered. Items outside the buffer use estimated heights.

```
VirtualOutputPanel
  ├─ _items: List<VirtualItem>
  ├─ _totalHeight: double              ← sum of all item heights (actual or estimated)
  ├─ _scrollY: double
  └─ _viewportHeight: double

VirtualItem
  ├─ HtmlSource: string
  ├─ Container: HtmlContainer?         ← null when outside buffer
  ├─ ActualHeight: double?             ← null until laid out
  ├─ EstimatedHeight: double           ← approximated from char count
  ├─ OffsetY: double                   ← vertical position (sum of previous heights)
  └─ IsInBuffer: bool
```

#### Rendering loop

```
On ScrollChanged or InvalidateVisual:
  1. Determine visible range: [scrollY, scrollY + viewportHeight]
  2. Expand by buffer: ±1 screenful
  3. Find items whose [offsetY, offsetY+height] intersects buffer range
  4. Activate: create HtmlContainer, lay out, mark IsInBuffer = true
  5. Deactivate: dispose HtmlContainer for items outside 2× buffer, keep ActualHeight
  6. Render active items at their offsetY using DrawingContext.PushTransform
```

#### Scrollbar behavior

```
_totalHeight = sum of all item heights (actual if measured, estimated if not)
ScrollBar.Maximum = _totalHeight - viewportHeight
```

As the user scrolls into previously-unmeasured areas, items get laid out and `_totalHeight` is refined. This produces the same slight height "correction" seen in lazy-loading virtual lists (acceptable behavior).

#### Append behavior

```csharp
void Append(string htmlFragment)
{
    var item = new VirtualItem { HtmlSource = htmlFragment };
    item.EstimatedHeight = EstimateHeight(htmlFragment);  // e.g., 18px per <div>
    item.OffsetY = _totalHeight;
    _items.Add(item);
    _totalHeight += item.EstimatedHeight;
    
    // If item is in buffer, activate immediately
    if (IsInBuffer(item)) Activate(item);
    
    InvalidateVisual();
}
```

#### Height estimation

Simple heuristic that avoids a full layout pass for off-screen items:

```csharp
double EstimateHeight(string html)
{
    // Count block-level elements as rough line count
    int divCount = Regex.Matches(html, "<div|<tr|<p").Count;
    int tableRows = Regex.Matches(html, "<tr").Count;
    return Math.Max(divCount * 20, tableRows * 22 + 32);
}
```

The estimate doesn't need to be accurate — it's corrected to `ActualHeight` once the item enters the buffer.

#### Integration with `HtmlPanel`

`VirtualOutputPanel` would be a separate control (not a subclass of `HtmlPanel`) that manages its own scrollbar and renders multiple `HtmlContainer`s in a single `Render()` call:

```csharp
public override void Render(DrawingContext context)
{
    context.FillRectangle(Background, Bounds);
    
    foreach (var item in GetBufferItems())
    {
        using (context.PushTransform(Matrix.CreateTranslation(0, item.OffsetY - _scrollY)))
        {
            item.Container.PerformPaint(context, new Rect(0, 0, Bounds.Width, item.ActualHeight));
        }
    }
    
    // Draw scrollbar
    _scrollBar.Render(context);
}
```

#### Trade-offs

| Aspect | Current | Feature B (Append) | Feature C (Virtual) |
|--------|---------|--------------------|---------------------|
| Append cost | O(n) per append | O(1) per append | O(1) per append |
| Layout on resize | O(n) | O(n) | O(visible) |
| Paint per frame | O(n) | O(n) | O(visible) |
| Memory for 100k lines | O(n) | O(n) | O(buffer size) |
| Text search across all | ✅ | ✅ | ❌ (off-buffer items not in DOM) |
| Theme change | O(n) | O(n) | O(visible) + re-estimate |
| Scroll position stability | ✅ | ✅ | ~✅ (estimates may shift) |
| Implementation complexity | baseline | medium | high |

---

### 5. Recommended Implementation Order

```
Phase 1  (1 day)    Fix CssBox.Paint() visibility check        → Feature A
                    Eliminates wasted per-frame draw calls

Phase 2  (1 week)   Add AppendHtml() to HtmlContainer/Control  → Feature B
                    Eliminates O(n²) cost for streaming output
                    Wire into EditorTabItemControl.AppendOutput()

Phase 3  (optional) Implement VirtualOutputPanel               → Feature C
                    Required only if 10k+ lines become an issue
```

Phases 1 and 2 together bring streaming output from O(n²) to O(1) per append and O(visible) per frame for the paint pass, which should be sufficient for typical DotPad usage (scripts producing up to a few thousand output lines).
