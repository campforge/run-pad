# Invoklet theme resources

Each interface theme defines the shared keyboard-focus ring in its theme dictionary:

| Resource | Type | Purpose |
| --- | --- | --- |
| `Invoklet.Focus.BorderBrush` | Brush | Main ring colour |
| `Invoklet.Focus.ContrastBrush` | Brush | Inner edge that remains visible on accent surfaces |
| `Invoklet.Focus.Thickness` | Thickness | Main ring thickness (default 2 DIP) |
| `Invoklet.Focus.ContrastThickness` | Thickness | Inner edge thickness (default 1 DIP) |
| `Invoklet.Focus.CornerRadius` | CornerRadius | Common outer radius (default 4 DIP) |
| `Invoklet.Focus.InnerCornerRadius` | CornerRadius | Inner radius (default 2 DIP) |

`InvokletTheme.axaml` uses one adorner template for shell controls. Resource changes and theme
switches update an already-focused control. Selection and primary-action colours are separate
from focus; text fields keep their caret and validation errors, and code editors keep their
own caret/selection behavior. Keyboard navigation shows the ring; pointer focus does not.

When changing ring colours, keep at least one edge at 3:1 contrast against panel, editor,
selection and primary-button surfaces, including hover and pressed states. Update both radii
when changing the total border thickness to keep the edges concentric.

Tool-panel selectors use `Invoklet.ToolTab.SelectedBackgroundBrush`,
`Invoklet.ToolTab.SelectedForegroundBrush` and `Invoklet.ToolTab.SelectedBorderBrush`.
These are separate from row/document selection accents so tool views can use a softer highlight.
