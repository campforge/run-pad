# AvaloniaEdit (Invoklet fork)

This folder and [`../AvaloniaEdit.TextMate`](../AvaloniaEdit.TextMate) are a fork of
[AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) and AvaloniaEdit.TextMate — the text editor
control for Avalonia, itself a port of AvalonEdit from the SharpDevelop project.

- **Forked from:** the `Avalonia.AvaloniaEdit` / `AvaloniaEdit.TextMate` 12.0.0 NuGet packages.
- **License:** MIT, the upstream license — see [`LICENSE`](LICENSE) (Copyright (c) 2017 Eli Arbel) and
  the SharpDevelop copyright headers kept in the source files. This license, not Invoklet's GPL-3.0,
  applies to the fork's original code.
- **Why a fork:** Invoklet's editor needs changes to the control itself that could not wait for
  upstream releases. Invoklet references both projects directly instead of the NuGet packages.
- **What Invoklet changed:** completion-list behavior (soft selection, commit on Tab and Enter,
  CamelHumps matching of word starts), font
  rendering, clipboard handling on macOS, keeping TextMate highlighting on the UI dispatcher, and
  completion/insight popups that take the transform of the editor they open from, and
  `TextEditor.ScrollToVerticalOffset`/`ScrollToHorizontalOffset`, which upstream leaves empty,
  a compact full-width search template with inline options and a second replacement row, and
  parameter info (`OverloadViewer`) that draws its `CornerRadius`.
  `git log -- src/controls/AvaloniaEdit src/controls/AvaloniaEdit.TextMate` shows every change.
- **Tests:** the upstream NUnit suite is kept unchanged in
  [`tests/AvaloniaEdit.Tests`](../../../tests/AvaloniaEdit.Tests); Invoklet's own tests for the fork live
  in `tests/Invoklet.Runtime.UI.Tests`.

## Working on the fork

- Prefer putting Invoklet behavior in `Invoklet.Avalonia.CodeTextEditor`; change the fork only when the
  control itself has to change.
- Keep upstream's style: nullable reference types stay disabled here, and existing license and
  copyright headers stay in place.
- A fix that is not Invoklet-specific is worth offering upstream as well.
