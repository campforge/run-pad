# Changelog

Notable changes in each RunPad release. Downloads are on the
[releases page](https://github.com/campforge/run-pad/releases).

## [Unreleased]

### Changed

- The main menu has a conventional layout — File, Edit, View, Run, Tools and Help — the same on
  every system. Every toolbar button and shortcut is now in the menu too: undo and clipboard
  commands, formatting, zoom, running and debugging. Dependencies, Assembly Explorer and Preferences
  moved to **Tools**, and **Help** links to the user guide, the release notes and the issue tracker.
- Every keyboard shortcut can be changed in **Preferences → Keyboard**: the page now lists all
  commands — including the editor's (completion, Go to Definition…) and the Assembly Explorer's —
  grouped by area, with **Reset** and a warning when two commands share a key.
- New default shortcuts follow LINQPad: **Stop** is **Shift+F5** (was Esc), **Run to Cursor** is
  **Ctrl+F10** and **Ctrl+R** shows or hides the output panel. Shortcuts you changed before are kept.
- On macOS, shortcuts use the Command key where that is the Mac convention (⌘S, ⌘N, ⌘W, ⌘, for
  Preferences…), and ⌘ + mouse wheel zooms the editor.
- RunPad's own analyzers (`RPAD…` warnings) are now an experimental feature, off by default. Turn
  them on in **Preferences → Experimental**.

### Fixed

- In the light theme, the children of a selected item in the Preferences tree and in the file
  tree no longer turn white.

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
  `Util.CurrentQueryPath` and wider LINQPad compatibility.
- **LINQPad import:** File → Import LINQPad Query... and `runpad convert` turn `.linq` files into
  `.rpad` scripts.
- **NuGet manager** in the style of LINQPad's, with package source configuration and authentication.
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
- `.Dump()` overloads compatible with LINQPad and documented `DumpOptions`.
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

[0.4.1]: https://github.com/campforge/run-pad/releases/tag/v0.4.1
[0.4.0]: https://github.com/campforge/run-pad/releases/tag/v0.4.0
[0.3.0]: https://github.com/campforge/run-pad/releases/tag/v0.3.0
[0.2.0]: https://github.com/campforge/run-pad/releases/tag/v0.2.0
