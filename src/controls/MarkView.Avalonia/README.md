# MarkView.Avalonia (Invoklet fork)

This folder and [`../MarkView.Avalonia.SyntaxHighlighting`](../MarkView.Avalonia.SyntaxHighlighting)
are a fork of [MarkView.Avalonia](https://github.com/Kryptos-FR/MarkView.Avalonia) and
MarkView.Avalonia.SyntaxHighlighting — a Markdig-based Markdown viewer control for Avalonia 12, with
TextMate highlighting for code blocks.

- **Forked from:** the `v12.3.0` tag (commit `a74e35b`), the source of the MarkView.Avalonia /
  MarkView.Avalonia.SyntaxHighlighting 12.3.0 NuGet packages. The optional Math, Mermaid and Svg
  extensions are not included.
- **License:** MIT, the upstream license — see [`LICENSE.md`](LICENSE.md) (Copyright (c) 2026 Nicolas
  Musset) and the copyright headers kept in the source files. This license, not Invoklet's GPL-3.0,
  applies to the fork's original code.
- **Why a fork:** Invoklet's Markdown preview of a script needs changes to the control itself, such as
  mapping rendered blocks back to source lines so the preview can scroll with the editor. Invoklet
  references both projects directly instead of the NuGet packages.
- **What Invoklet changed:** the project files (Invoklet's central package versions, no NuGet packaging
  or lock files), CRLF line endings and Invoklet's formatting rules, the fork's new path in one
  test that reads the theme file from disk, and the source line of each rendered block
  (`AvaloniaRenderer.SourceBlocks`, `MarkdownViewer.GetSourceLineAtTop`/`ScrollToSourceLine` and
  its `ScrollChanged` event), which lets the preview scroll with the editor, and lazy code blocks
  (`MarkdownViewer.LazyCodeBlocks`, `LazyCodeLines`): a long code block is split into chunks of
  plain text that are highlighted only near the viewport, so the theme's code-block text style
  applies to any `TextBlock` inside the block. `git log --
  src/controls/MarkView.Avalonia src/controls/MarkView.Avalonia.SyntaxHighlighting` shows every
  change.
- **Tests:** the upstream xunit v3 suites are kept in
  [`tests/MarkView.Avalonia.Tests`](../../../tests/MarkView.Avalonia.Tests) and
  [`tests/MarkView.Avalonia.SyntaxHighlighting.Tests`](../../../tests/MarkView.Avalonia.SyntaxHighlighting.Tests),
  with upstream's shared helpers in `tests/MarkView.Avalonia.Tests/Shared`. They run through the VSTest
  adapter like the rest of the solution, and `MarkView.Avalonia.Tests` runs its test collections one
  after another (`xunit.runner.json`): run in parallel, a test now and then fails in cleanup with
  "the calling thread cannot access this object", and upstream's own runner silently drops one.

## Working on the fork

- Prefer putting Invoklet behavior in Invoklet's own projects; change the fork only when the control
  itself has to change.
- Keep upstream's style and the existing license and copyright headers.
- A fix that is not Invoklet-specific is worth offering upstream as well.
