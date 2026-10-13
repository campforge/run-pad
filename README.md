# RunPad

A desktop C# "scratchpad"/scripting IDE built on Avalonia UI (net10.0) — in the same space as
LINQPad, RoslynPad and NetPad. It edits and runs `.rpad` files: self-documenting Markdown documents
with a YAML front matter header and one executable code block.

- Architecture, build/test commands, code style — `docs/ARCHITECTURE.md`.
- User guide — [documentation source](docs/site/index.md); [GitHub Pages setup](docs/GITHUB-PAGES.md).
- Current tasks — `TODO.md`; root-cause analysis and code-audit results — `ANALYSIS.md`.
  Full history of closed items from 2026-07 (passes 1-58, items 1-84) — `docs/archive/`.
- Full `.rpad` format specification — `docs/SpecificationRPad.md`.

Why Avalonia UI and not another UI framework? I was looking for an open, community-maintained UI
that I could fork, modify, and send pull requests to.

C# specifics
- netcoredbg wasn't picked at random: it's currently the best open-source .NET debugger,
  maintained by the community and Samsung.

## Installation

Prebuilt binaries are on the [releases page](https://github.com/coddwrench/run-pad/releases/latest).

**Windows** — download `RunPad-win-x64.msi` and run it (adds a Start Menu shortcut, an uninstaller
under "Add or remove programs", and the next version installs over it). Or `RunPad-win-x64.zip` — a
portable, install-free option: unzip and run `RunPad.exe`.

**Linux** — `runpad_<version>_amd64.deb` (`sudo dpkg -i runpad_*.deb`, or via your software center —
puts `runpad` on PATH and adds a menu entry). Or `RunPad-x86_64.AppImage` — `chmod +x` and run it,
nothing is installed system-wide. Or `RunPad-linux-x64.tar.gz` — unpack and run `RunPad`.

**macOS** — download the disk image for your Mac: `RunPad-osx-arm64.dmg` for Apple Silicon (M1 and
later) or `RunPad-osx-x64.dmg` for Intel (Apple menu → About This Mac shows which). Open it, drag
`RunPad.app` onto the `Applications` folder shown next to it, eject the disk image, then start RunPad
from Applications.

The app is only ad-hoc signed — not signed with an Apple Developer ID certificate or notarized yet —
so on first launch macOS may refuse to open it because it can't verify the developer. If you downloaded it from the
releases page above, open System Settings → Privacy & Security and click "Open Anyway" next to the
RunPad message, then confirm. macOS offers that button only for a while after a blocked launch, and
depending on your macOS version and security settings may not offer it at all. The disk image
contains a `Read Me First.txt` with the same steps.

## Inspiration and components used

- Debugger: [netcoredbg](https://github.com/Samsung/netcoredbg) (via DAP).
- Design references: [NetPad](https://github.com/tareqimbasher/NetPad),
  [RoslynPad](https://github.com/roslynpad/roslynpad).
- DI: [Avalonia DI guide](https://docs.avaloniaui.net/docs/guides/implementation-guides/how-to-implement-dependency-injection)
  (`Microsoft.Extensions.DependencyInjection`, composition root — `App.axaml.cs`).

## Project goals — what works and what doesn't yet

A summary checklist drawn from `TODO.md`/git history (details for any item are in
`docs/archive/TODO-2026-07.md`).

### Done

- [x] **`.rpad` format**: YAML front matter (version, language, targetSdk, kind, packages,
  references, namespaces) + a Markdown body with one executable code block; a hand-rolled
  position-based parser/writer, specification under `docs/`.
- [x] **Three script modes (kind)**: `Statements` / `Program` / `Expression`, each with correct
  line mapping for the debugger.
- [x] **Running**: compiles in-process with Roslyn (no MSBuild); NuGet packages (restored
  in-process) and assembly references come from front matter (project references are not
  supported); a build cache (skips restore/compile when inputs are unchanged, invalidated on
  compile error), persistent build directories with automatic
  cleanup of old ones.
- [x] **Debugger (netcoredbg/DAP)**: breakpoints (including conditional and hit-count), verified
  status with per-state icons, stepping (over/in/out), pause, run-to-cursor, editing breakpoints
  mid-session, local variables with lazy expansion, watch expressions, call stack, threads,
  break-on-exceptions (all/unhandled).
- [x] **`.Dump()`**: HTML output (tables, nested objects/collections, dictionaries), `Dump()`/
  `Dump(label)`/`Dump(depth)`/`DumpOptions` overloads (depth, row limit, collapse, private members,
  etc.), `DumpContainer` with in-place updates, auto-title derived from the expression text; lazy
  repaint of the output panel.
- [x] **IntelliSense (Roslyn, own pipeline)**: autocomplete with 24/7 auto-invoke, member/static/
  namespace access (`.`, `?.`), context-aware filtering (strings/comments/declaration names/generic
  arguments), scope-based sorting, overload collapsing, own members shown in bold, auto-import of
  types from referenced packages (auto-`using` on accept), override stubs with generated bodies,
  snippets, keyword filtering based on syntactic position.
- [x] **Signature help**: methods and constructors, overload selection based on argument types,
  active-parameter highlighting, XML-doc summaries; quick info on hover; syntax highlighting and
  summaries inside tooltips.
- [x] **Editor diagnostics**: background squiggles, semantic highlighting, a quick-fix lightbulb
  backed by real Roslyn `CodeFixProvider`s (auto-`using` goes into front matter), icons and
  severity colors, Format Document.
- [x] **Editor**: a combined breakpoint/line-number margin, code folding (brace-based and, for VB,
  keyword-based — the strategy is supplied by the language), matching-brace highlighting, current
  debug-line highlighting, TextMate syntax highlighting, a per-language editor profile (triggers,
  brace pairs, identifiers, indentation) supplied by the language plugin — the control itself is
  language-agnostic.
- [x] **A second language — Visual Basic**: compile, run, debug, kind transforms, folding (no
  IntelliSense for VB yet).
- [x] **UI**: tabs, a file tree (create/rename/delete files and folders, per-language icon, "reveal
  in file manager" on every OS), a Dependencies window (NuGet search across feeds from
  `nuget.config`, local package cache), Preferences (customizable hotkeys, font, themes, dotnet/
  netcoredbg paths), light/dark themes (Fluent/Simple) sharing a single token set.
- [x] **RunPad.Cli**: a console runner for `.rpad` files with no GUI/debugger (see the section
  below), rendering `.Dump()` output in the terminal.
- [x] **Tests**: 5 test projects, ~395 tests, including headless UI tests covering the editor's
  full keyboard pipeline; CI (format + build + test) on ubuntu/windows/macos.

### Not done yet / planned (numbers refer to `TODO.md` items)

- [ ] Auto-format on type (#17) and format-on-selection.
- [ ] Soft selection, MRU and per-item commit characters in autocomplete (#60), target-type
  preselection; full fuzzy filtering (#18) — blocked on the editor fork (#61).
- [ ] Set Next Statement in the debugger (#22).
- [ ] Choosing a kind when creating a new file (#35).
- [ ] First-run setup wizard — auto-detect/download SDK and netcoredbg (#71).
- [ ] Enabled/disabled state for breakpoints (#83.3).
- [ ] Refactorings and custom analyzers on top of the lightbulb (#74).
- [ ] Exporting results (Excel/HTML), database connections, an IL/syntax-tree viewer, rename
  symbol, find references, CodeLens (#62).
- [ ] Interactive input from the output panel (an stdin channel to the script process), path
  macros in references, a `.linq` → `.rpad` converter (#63).
- [ ] Languages as real plugins (assemblies loaded at runtime, no RunPad rebuild needed) — a goal;
  groundwork so far: #87.
- [ ] New languages on the roadmap: F#, SQL (MVP — in-memory SQLite), Go, PowerShell, Python, C,
  C++, Rust — architectural scouting only so far, nothing started (see TODO.md → "New languages on
  the roadmap" for details on each).
- [ ] Preferences reorganization: group settings by runtime family (e.g., a single ".NET" group
  with SDK/debugger paths shared by C#/VB/a future F#) plus per-language syntax highlighting —
  architectural scouting done, not started (see TODO.md).
- [ ] Restoring the previous session's tabs, workspaces (#62).
- [ ] Tech debt from the 2026-07-16 audit: collapsing C#/VB backend duplication (#86), build-cache
  thread safety (#85), testability of process paths (#88), and others — see `TODO.md`.

## File format

The format is built on top of Markdown — this lets a script file be self-documenting: YAML front
matter (version, language, target SDK, kind, packages, references, usings) plus a single
```` ```csharp run ```` block with the code itself; everything else in the surrounding Markdown is
preserved as-is.

## RunPad.Cli — running a `.rpad` file from the console without a GUI

`src/main/RunPad.Cli` is a lightweight console runner: it compiles and executes a `.rpad` file's run
block directly, without Avalonia and without the debugger (the same "fast path" as the Run button in
RunPad itself, minus DAP/`netcoredbg`). Useful for CI/automation, or whenever a full editor isn't
needed. Supports both `language: csharp` and `language: vb` — front matter (`packages`/`references`/
`namespaces`/`kind`) is honored the same way it is in the GUI.

Running straight from source:

```bash
dotnet run --project src/main/RunPad.Cli -- path/to/script.rpad
```

Or build and run separately (the assembly is named `runpad.dll`):

```bash
dotnet build src/main/RunPad.Cli -c Release
dotnet src/main/RunPad.Cli/bin/Release/net10.0/runpad.dll path/to/script.rpad
```

(There's also a native apphost, `runpad`/`runpad.exe`, that runs without a `dotnet` prefix — but
only if .NET is installed somewhere the apphost knows to look; on a non-standard install (e.g. via
Homebrew/Linuxbrew) it won't find the runtime, so use the `dotnet ... .dll` form above instead — that
one always works.)

Normal script output and `Console.Error`/compile errors go to stdout/stderr respectively; `.Dump()`
renders directly in the terminal via [Spectre.Console](https://spectreconsole.net/) — tables, colors
by value type (strings/numbers/null/enum/date, etc.), nested collections/objects become nested
tables. Color is enabled automatically only when output is actually going to a terminal (not a
file/pipe). Exit codes: `0` — success and nothing written to stderr; `1` — error (bad arguments,
file not found or not parseable as `.rpad`, no SDK installed, compile error, or the script itself
wrote something to stderr during execution); `130` — interrupted with Ctrl+C.

## License

Copyright (C) 2026 Vadim ([coddwrench](https://github.com/coddwrench))

Licensed under the GNU General Public License v3.0 — see [`LICENSE`](LICENSE) for the full text.

## Known issues

The current list of bugs and tech debt lives in `TODO.md` (items 85+ are results of the 2026-07-16
code audit, details in `ANALYSIS.md`). The IntelliSense reference-resolution bug on Windows
previously described here (`error CS0009` about `*.Native.dll`) has been fixed — it now checks the
PE header instead of matching on file name, see archived TODO #44.

## Quick TODO
- Could try implementing https://github.com/IronLanguages/ironpython3, basic functionality only.
- Other places to look for ideas:
  - https://github.com/mono/monodevelop
  - https://github.com/icsharpcode/sharpdevelop
- Debugging ideas:
  - bash debugging: https://github.com/rogalmic/vscode-bash-debug
  - PowerShell debugging: https://github.com/PowerShell/vscode-powershell
