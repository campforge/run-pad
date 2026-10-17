# Changelog

Notable changes in each RunPad release. Downloads are on the
[releases page](https://github.com/campforge/run-pad/releases).

## [Unreleased]

### Fixed

- Editor diagnostics and refactorings use the selected .NET target's C# version and conditional
  compilation symbols, matching compilation when switching targets, and update as soon as another
  SDK is selected instead of after the next edit.
- The editor no longer reports errors for code completed by the .NET source generators, such as
  `[GeneratedRegex]` methods and System.Text.Json serializer contexts, and completes their members.
  The errors and warnings those generators report themselves, such as an invalid regex pattern, are
  now shown in the editor and listed with their line in the build output.
- In Program-kind scripts, lightbulb actions such as Add braces, Add accessibility modifiers or Wrap
  expression no longer indent the code they change one level too deep.
- Clicking a lightbulb entry no longer occasionally does nothing when the analysis of an earlier
  edit finishes after the list was shown.

## [0.6.0] — 2026-10-17

### Added

- **Preferences → Interface → Interface scale** makes everything in RunPad's windows larger or
  smaller — text, icons, spacing and click targets of the panels, tabs, results, debugger, dialogs,
  menus, tooltips and code completion, and the code itself. Choose 90% to 200%; **Reset to 100%**
  brings back the usual size. The menu bar at the top of the screen on macOS stays as the system
  draws it.

### Changed

- The debugger keeps Threads and Call Stack in permanent tables with compact text headings and
  a draggable divider, beside Locals and Watch.
  An always-visible settings button opens exception break options. Panel views use soft
  theme-defined selectors. Watch actions use compact plus/minus buttons with tooltips; the settings
  icon has a trailing inset.
- The status bar uses a neutral theme background and readable text, with a small coloured state
  indicator. Light themes use darker status colours. Scripts that have not run yet show Ready
  with neutral grey text and indicator.

- Keyboard focus now uses one rounded theme-defined ring across buttons, tabs, dropdowns, trees
  and text fields. Each theme controls its colours, thickness and corner radius independently
  of selection colours; primary buttons no longer add a second focus border.

- Editor preferences are grouped into font and zoom, display, editing, and new-file defaults,
  with aligned fields and the same settings and keyboard behavior.

- Code zoom (**View → Zoom In/Out**, Ctrl+wheel or Cmd+wheel on macOS, and the percentage in the
  status bar) is now one setting for all editors: every open tab and every tab opened later shows
  code at the same zoom, and it is kept when RunPad restarts. It works on top of the interface
  scale — 100% is the interface-scaled font size — and the wheel moves through the same steps as
  the menu. **Preferences → Editor → Code zoom** shows it and resets it on its own. Files opened in
  their own editor window now use the editor font, size and zoom too.
- In dialogs, the button that **Enter** presses (OK, Save, Rename…) is now shown in the theme's
  accent colour, with a visible ring when it has keyboard focus, so the main action stands out;
  **Enter** and **Escape** work as before. The dividers between the side panel, the editor and the
  Results panel are now thin lines; they are dragged in the same place as before.

### Fixed

- IntelliSense checks framework APIs against the script's selected .NET target when its reference
  pack is available, including when a script without packages is first opened, before any run.

- IntelliSense no longer reports duplicate-type errors when a NuGet package includes an assembly
  also provided by the framework; package updates use the same overlap selection as compilation.

- Editor diagnostics, quick fixes and refactoring titles stay in English, matching the interface,
  instead of following the operating system language.

- Panel dividers now meet their adjacent panels without empty bands on either side of the line,
  including in Preferences. The invisible drag target still extends over both panel edges.
- Debugger tabs wrap when the panel is narrow, keeping every tab visible and clickable.

- **Queries** no longer lists folders whose names start with a dot, such as `.obsidian` or `.git`,
  or anything inside them, so a queries folder that is also an Obsidian vault or a Git repository
  shows only your scripts. Folders and files on disk are not touched.
- **Queries** now updates when a folder is created, renamed, moved in or deleted outside RunPad —
  in Finder, a terminal or Obsidian — also for folder names with a dot, such as `project.v2`. Before,
  only changes to `.rpad` files were noticed. Changes inside dot folders such as `.obsidian` and
  to other kinds of files no longer cause a refresh.
- Easier to read: the hit-count hint of the breakpoint condition dialog wraps instead of being
  cut off, a long version in **About RunPad** ends with "…" and shows in full as a tooltip, error
  messages name what failed and show the details in normal text instead of all red, and grey
  secondary text is a little darker in the VS Code, Rider, Visual Studio and RunPad Warm light
  themes so that it stays readable. Selected items in Rider Light, RunPad Midnight and RunPad Warm
  Light are easier to read as well.

## [0.5.0] — 2026-10-15

### Added

- A Program script's `Main` can take the command-line arguments and a cancellation token —
  `Main(string[] args)`, `Main(CancellationToken ct)` or `Main(string[] args, CancellationToken ct)` —
  and can return `int` or `Task<int>` as the exit code. With several `Main` overloads, the one that
  takes the most is called.
- `Util.CancellationToken`, cancelled when you press **Stop**. A script that uses it — or a `Main`
  that takes a token — gets up to 3 seconds to close files, connections and child processes before
  RunPad ends it; other scripts still stop at once.
- A new Program script starts with a `Main` method, with the caret inside it. Switching a script
  to **Program** wraps its code in `Main` — `async Task Main()` when the code awaits — and keeps
  methods and types after it; one **Undo** brings the old code back. Visual Basic scripts get a
  `Module` with `Sub Main()`.
- An **Examples** tab next to **Queries** in the panel on the left, with sample scripts that come
  with RunPad. Clicking one opens it as a new, unsaved script: it does not run, and nothing is
  written until you save it, which asks where to put the copy.

### Changed

- A script runs on the .NET version its `targetSdk` names, in the RunPad window and the
  command-line runner alike. The toolbar's SDK list shows it — **latest (.NET 10.0.100)** keeps
  `targetSdk: latest`, any other entry writes its version — and a version that isn't installed runs on
  the newest SDK with a warning in Results and *(not installed)* in the status bar. Before, `targetSdk`
  was only shown and the newest SDK always ran. New scripts get `targetSdk: latest`.
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
- The panel on the left has **Queries** and **Examples** tabs, styled like the editor's document tabs,
  instead of the *FILES* heading; the file tree is on **Queries**, with everything it did before.
- The lightbulb's column is narrower: just wide enough for the icon, which keeps its size and its
  click area around it.

### Fixed

- When the debugger stops on an exception, RunPad now says so: the status bar shows
  *Paused on FormatException* instead of a plain *Paused*, and the **Debugger** tab shows the
  exception's type, message and stack trace, and whether anything catches it. Stepping from an
  exception nothing catches no longer leaves the debugger waiting with no reply: you are told the
  step isn't possible there and can **Continue** or **Stop**. A debug run that ends with such an
  exception finishes as *Unhandled FormatException*, not *Success*.
- A script's `args` no longer contains RunPad's own internal arguments (`--pid …`); it is empty.
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
- Code is shown in a real monospace font on macOS: the editor's default font resolved to
  Helvetica there. With **Preferences → Editor → Font family** left at `monospace`, RunPad now uses
  a fixed-width font that comes with the system — Menlo on macOS, Cascadia Mono or Consolas on
  Windows, DejaVu Sans Mono or a similar font on Linux — in the editor, the quick-fix menu, the IL
  view and the breakpoint condition, rename and package dialogs. A font you chose yourself is kept;
  one that isn't installed falls back to that default. Font sizes are unchanged.

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

[0.5.0]: https://github.com/campforge/run-pad/releases/tag/v0.5.0
[0.4.2]: https://github.com/campforge/run-pad/releases/tag/v0.4.2
[0.4.1]: https://github.com/campforge/run-pad/releases/tag/v0.4.1
[0.4.0]: https://github.com/campforge/run-pad/releases/tag/v0.4.0
[0.3.0]: https://github.com/campforge/run-pad/releases/tag/v0.3.0
[0.2.0]: https://github.com/campforge/run-pad/releases/tag/v0.2.0

[0.6.0]: https://github.com/campforge/run-pad/releases/tag/v0.6.0
