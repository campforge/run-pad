# Changelog

Notable changes in each RunPad release. Downloads are on the
[releases page](https://github.com/campforge/run-pad/releases).

## [Unreleased]

### Added

- A new Program script starts with a `Main` method, with the caret inside it. Switching a script
  to **Program** wraps its code in `Main` — `async Task Main()` when the code awaits — and keeps
  methods and types after it; one **Undo** brings the old code back. Visual Basic scripts get a
  `Module` with `Sub Main()`.

### Changed

- The File menu's import command is now called **Import .linq Query...**.
- A new Statements or Expression script created from the file tree starts empty, like one created
  with **File → New File**.
- The editor's gutter is more compact and calmer: a thin line separates it from the code, the current
  line's row is highlighted, and breakpoints and the lightbulb are sized to the text. Fold arrows of
  open blocks appear while the pointer is over the fold column; collapsed blocks always show theirs.
  On a wrapped line, its number, breakpoint, lightbulb and fold arrow sit next to its first row — the
  lightbulb used to appear lower, in the middle of the whole line.
- With the caret at a brace, both braces of the pair are highlighted in the theme's colors, and the
  guide between them is emphasized in the code and in the gutter.

### Fixed

- A .NET SDK installed by the first-run setup is found again after RunPad restarts — on macOS,
  also when RunPad is started from Finder or the Dock. Its path is now saved in
  **Preferences → Languages → .NET**.
- **Cancel** in the first-run setup stops the installer instead of leaving it running in the
  background, so a second install can no longer start next to the first.
- Braces opened and closed on the same line, such as `if (ok) { Run(); }`, no longer get a fold
  marker in the editor's margin; braces spanning several lines fold as before.
- The quick-fix lightbulb is easier to click: an invisible square around it, larger than the icon,
  opens its menu, and it stays the right size when you zoom. Clicks beside the square no longer do,
  and its menu now opens right below the lightbulb — on macOS it could appear in the top-left corner
  of the screen instead.
- Completion, error highlighting and automatic `using`s now see the types of NuGet packages
  added as `latest`, with a floating version (`1.*`) or with a version range: they used to work
  only for an exact version, although such scripts compiled and ran. After a run, the editor uses
  the versions the restore actually chose, including the packages they depend on.

## [0.4.2] — 2026-10-14

A new main menu, the same on every system, and shortcuts you can change for every command. On
macOS, RunPad starts again: the 0.4.1 app quit as soon as it was opened.

### Changed

- The main menu has a conventional layout — File, Edit, View, Run, Tools and Help — the same on
  every system. Every toolbar button and shortcut is now in the menu too: undo and clipboard
  commands, formatting, zoom, running and debugging. Dependencies, Assembly Explorer and Preferences
  moved to **Tools**, and **Help** links to the user guide, the release notes and the issue tracker.
- Every keyboard shortcut can be changed in **Preferences → Keyboard**: the page now lists all
  commands — including the editor's (completion, Go to Definition…) and the Assembly Explorer's —
  grouped by area, with **Reset** and a warning when two commands share a key.
- New default shortcuts follow common IDE conventions: **Stop** is **Shift+F5** (was Esc), **Run to Cursor** is
  **Ctrl+F10** and **Ctrl+R** shows or hides the output panel. Shortcuts you changed before are kept.
- On macOS, shortcuts use the Command key where that is the Mac convention (⌘S, ⌘N, ⌘W, ⌘, for
  Preferences…), and ⌘ + mouse wheel zooms the editor.
- On macOS, the menu bar shows each command's shortcut, as the menu does on Windows and Linux, and
  holds only RunPad's own commands: no Hide, Show All, Quit or second About in the application
  menu, and no AutoFill, Start Dictation… or Emoji & Symbols in **Edit**. About is under **Help**,
  and **File → Exit** (⌘Q) quits, as on other systems; ⌘Q also works from Preferences, text editor
  windows and the Assembly Explorer.
- A shortcut changed in Preferences takes effect at once in every open window — the menu, text
  editor windows and the Assembly Explorer — without reopening them.
- RunPad's own analyzers (`RPAD…` warnings) are now an experimental feature, off by default. Turn
  them on in **Preferences → Experimental**.

### Fixed

- The macOS app starts again. In 0.4.1 it quit at once because it could not find its own .NET
  runtime inside the app bundle.
- Quitting asks about unsaved changes in text editor windows together with unsaved tabs, then
  quits; before, answering for a text editor window stopped the quit and RunPad stayed open.
- Updated a component of the in-app NuGet restore that had known denial-of-service
  vulnerabilities (System.Security.Cryptography.Pkcs 8.0.2).
- In the light theme, the children of a selected item in the Preferences tree and in the file
  tree no longer turn white.
- Preferences → Keyboard warns when a file-tree or Assembly Explorer shortcut is also used by a
  command the same keypress reaches, such as the file tree's Delete rebound to New File's key.

## [0.4.1] — 2026-10-14

The first public release.

### Changed

- RunPad is now developed in the open at [campforge/run-pad](https://github.com/campforge/run-pad),
  with a [roadmap](ROADMAP.md) and a [contributing guide](CONTRIBUTING.md).
- Every package ships the license texts and `THIRD-PARTY-NOTICES.txt`, which lists every
  third-party component with its license, next to the application.

### Fixed

- Quick fixes that add a `using` directive no longer fail with newer .NET SDKs, which report the
  inserted line as a character-level change.

## [0.4.0] — 2026-10-13

### Added

- A plain text editor window for configuration and other text files.
- The [user guide](https://campforge.github.io/run-pad-docs/), with screenshots and downloadable
  examples.
- The installers show an end-user license agreement.

### Changed

- The `RunPad.Runtime` library — the one your scripts are compiled against — is now licensed under
  MIT, so programs written in RunPad can be distributed under any license. RunPad itself stays under
  GPL-3.0.
- The status strip and the command-line runner report the script's real exit code: a script that
  calls `Environment.Exit(7)` shows *Exit code 7* instead of *Success*, and a failed compile shows
  *Compilation failed*. Text on standard error alone no longer fails a CLI run.
- Smoother typing in the script editor.
- Avalonia updated to 12.1.4.

### Fixed

- macOS app bundle layout and its ad-hoc signature verification.

## [0.3.0] — 2026-10-12

### Added

- **Editor:** Go to Definition and Go to Implementation (F12), Find References, smart rename,
  symbol occurrence highlighting, inlay hints, object initializer completion, Resolve All Usings
  (Ctrl+Alt+U), zoom in the status strip, and an option to accept completion when typing
  punctuation.
- **Diagnostics:** Roslyn IDE analyzers and refactorings in the lightbulb, grouped by kind with
  icons, and shown only on the caret's line; a master switch for code fixes and refactorings.
- **RunPad analyzers** (`RPAD…`): more than 500 checks for code that always throws at run time —
  constant arguments out of range, invalid literals, wrong argument validation — plus LINQ,
  performance and simplification hints.
- **Assembly Explorer** in the style of ILSpy: several assemblies at once, decompiled C# or IL,
  resources, tabs with back/forward history, search across types and members, Save Code, and Analyze
  (Used By, Uses, Instantiated By, Implemented By, Overridden By and more).
- **Runtime helpers:** interactive input (`Util.ReadLine`, `Util.GetPassword`),
  `Util.ProgressBar`, `Util.Cache`/`CacheAsync`, file helpers (`Util.SaveString`, `LoadString`, …),
  `Util.CurrentQueryPath` and wider compatibility with imported `.linq` scripts.
- **`.linq` import:** a File menu command and `runpad convert` turn `.linq` files into
  `.rpad` scripts.
- **NuGet manager** with package source configuration and authentication.
- **First-run setup** that finds or installs the .NET SDK and the netcoredbg debugger.
- **Installers:** MSI for Windows, `.deb` and AppImage for Linux, unsigned DMG images for Apple
  Silicon and Intel Macs, and portable archives.
- **File tree:** New File, drag and drop to move files and folders.
- The native macOS title bar and system menus; the same application menus on every desktop
  platform; the app icon on every window.

### Changed

- Scripts compile in-process with Roslyn instead of MSBuild — faster runs, with build output cached
  between runs of an unchanged script.
- Program-kind C# scripts are wrapped in a generated class, so `Main` may be static or not and
  return `void` or `Task`.
- Roslyn updated to 5.9, NuGet to 7.9, ICSharpCode.Decompiler to 11.1; `RunPad.Runtime` no longer
  depends on the System.Text.Json package.

### Fixed

- Unsaved work is safe: closing, renaming, moving or deleting a tab writes its changes first, and a
  failed write keeps the tab and its text.
- A file with invalid front matter opens as plain text and is never run with default settings.
- macOS debugging: launching under the debugger and breakpoints in Roslyn-compiled scripts.
- Results rendering after display scaling changes; font rendering in the editor.
- File tree errors on unreadable folders no longer crash the app.

## [0.2.0] — 2026-07-18

### Added

- **Visual Basic** scripts: compile, run and debug, with keyword-based folding.
- **Debugger** over the Debug Adapter Protocol, with conditional breakpoints and hit counts; badges
  and outlines for conditional and unverified breakpoints.
- **IntelliSense:** real Roslyn code fixes, automatic import of namespaces (including types from
  NuGet packages), completion icons, overload counts, override stubs, contextual keywords, type
  filtering inside generic arguments, and snippets.
- Formatting and navigation commands with keyboard shortcuts.
- **Command-line runner** (`RunPad.Cli`) for running `.rpad` files from a terminal.
- More `.Dump()` overloads and documented `DumpOptions`.
- Path macros in references, such as `$(CurrentQueryPath)`.
- File tree with New Folder and per-language icons; New RPad File for C# and Visual Basic.
- Save, Save As (Ctrl+Shift+S), confirmation before closing unsaved tabs, restoring open tabs on
  start, and a drop-down of all open tabs.
- A persistent build cache for compiled scripts.

### Changed

- The editor is built on a fork of AvaloniaEdit and AvaloniaEdit.TextMate.
- A shared base for every compiled .NET language host.
- C# scripts get the SDK's implicit usings in both the compiler and IntelliSense.

### Fixed

- Stale output after a failed build; debugger line offsets in Program scripts; duplicate app icon on
  Linux; completion races and accidental replacement of declaration names.

## [0.1.0] — 2026-07-05

### Added

- Avalonia 12 desktop app with a C# editor, syntax highlighting and themes.
- Running scripts and rendering `.Dump()` results as HTML tables.
- First C# IntelliSense: completion, signature help and diagnostics.
- First debugger integration with breakpoints.
- The `.rpad` file format: Markdown with YAML front matter and one runnable code block.
- A scripts folder shown as a file tree that follows changes on disk.
- Preferences, keyboard shortcuts, logs and the app icon.

## [0.0.0] — 2024-04-30

- The project started as **DotPad**: an Avalonia desktop scratchpad for C# with a code editor
  based on AvaloniaEdit and an HTML renderer for results.

## Before that

The first plan was to extend RoslynPad rather than start from scratch: a
[fork of RoslynPad](https://github.com/hobobyte/roslynpad) was meant to gain a debugger and the
other features RunPad has today. The work in the fork, in April 2024, prepared the ground:

- removed the Windows-only WPF front end (its editor, dialogs and glyphs), keeping only the
  cross-platform Avalonia one;
- dropped support for old .NET SDKs and added detection of the installed SDKs;
- removed problematic parts, refactored, and moved shared helpers into a utilities project.

Even this groundwork showed how large the rest of the changes would be — the debugger and the other
features had not been started yet — so writing a new application from scratch turned out to be the
better choice. RunPad began as DotPad two days after the last commit to the fork.

[0.4.2]: https://github.com/campforge/run-pad/releases/tag/v0.4.2
[0.4.1]: https://github.com/campforge/run-pad/releases/tag/v0.4.1
[0.4.0]: https://github.com/campforge/run-pad/releases/tag/v0.4.0
[0.3.0]: https://github.com/campforge/run-pad/releases/tag/v0.3.0
[0.2.0]: https://github.com/campforge/run-pad/releases/tag/v0.2.0
