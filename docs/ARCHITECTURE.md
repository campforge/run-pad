# RunPad architecture

How the solution is put together, how to build and test it, and the conventions the code follows.
For how to send changes, see [`CONTRIBUTING.md`](../CONTRIBUTING.md).

RunPad is a desktop C# and Visual Basic scratchpad built on Avalonia UI (net10.0). It edits and runs
`.rpad` files — Markdown documents with a YAML front matter header and one executable code block —
compiling them in-process with Roslyn against the installed .NET SDK, and debugging them through
`netcoredbg` over the Debug Adapter Protocol (DAP).

## Commands

```bash
dotnet restore
dotnet build                                              # whole solution (RunPad.sln)
dotnet build --configuration Release -warnaserror         # what CI runs
dotnet format --verify-no-changes --verbosity diagnostic  # CI formatting gate (run `dotnet format` to fix)

dotnet test                                               # all test projects
dotnet test tests/RunPad.Runtime.Tests                    # a single test project
dotnet test --filter "FullyQualifiedName~DumpRendererTests" # a single test class/name

dotnet run --project src/main/RunPad/RunPad.csproj        # run the app (the VS Code "build"/"watch" tasks do the same)
```

CI (`.github/workflows`) runs the format check, then build and test on Ubuntu, Windows and macOS.

## Projects and layering

The solution is layered as `src/core` → `src/languages` → `src/main`, plus independent `src/controls`
and `src/runtime`. Dependencies only go one way: `main` depends on `languages`, `core` and `controls`,
never the reverse.

**`src/core/RunPad.Contracts`** — the language-plugin abstraction. `ILanguageHost` is the interface every
language back end implements (compile, run, debug, breakpoints, memory dump, IntelliSense), gated by the
`[Flags] LanguageHostCapabilities` enum checked via `Supports()`/`EnsureCapability()`. Hosts are created by
an `ILanguageHostFactory` and looked up by language id through `LanguageHostRegistry`. `csharp` and `vb`
are registered (`CSharpLanguageHostFactory`/`VbLanguageHostFactory`, wired up in `App.axaml.cs`);
languages are still plugged in with a plain `ProjectReference` plus manual registration, not a runtime
plugin system.

**`src/core/RunPad.Format.RPad`** — parses and writes the `.rpad` format: YAML front matter
(`RpadFrontMatter`: version, language, target framework, `kind`, packages, references, usings) followed by
a single fenced code block (```` ```csharp run ```` or ```` ```vb run ````), with the surrounding Markdown
preserved verbatim (`RpadParser`/`RpadWriter`/`RpadDocument`). The full format is specified in
[`SpecificationRPad.md`](SpecificationRPad.md). The parser is hand-rolled (no regex or Markdown library)
and position-based, so changes there need care around CRLF/LF and the exact fence detection.

**`src/core/RunPad.Build.Core`** — process-running plumbing (`ChildProcess`, `ProcessRunners`) shared by
compilation and execution for every language.

**`src/core/RunPad.Debug`** — the DAP client that talks to the external `netcoredbg` process
(`DapSession`; `Protocol/` holds the request/response types). Shared by every compiled .NET language host:
debugging works against compiled IL and PDB, whatever the source language.

**`src/languages/RunPad.Languages.DotnetCommon`** — compile/run/debug orchestration shared by every .NET
language compiled the same way (in-process Roslyn — RunPad does not use MSBuild — and debugging via
`RunPad.Debug`). `CompiledDotnetLanguageHostBase` implements the part of `ILanguageHost` that is identical
across languages (Template Method); a concrete host only supplies `Language`/`Capabilities`, a
`DotnetLanguageProjectProfile` (source file extension, default imports, import syntax), its compiler
(`CreateCompiler`) and the two kind-transform hooks (`ApplyKindTransform`/`CalcKindOffset`).
`Compiler/RoslynScriptCompiler` is the shared compiler pipeline: the target framework's reference pack,
in-process NuGet restore (`NuGetRestorer`, NuGet.Commands), source generators, and a DLL with portable PDB
and runtimeconfig in `OutputDirectory/bin` — the same layout `dotnet build` produces.
`CSharpScriptCompiler`/`VbScriptCompiler` supply the compiler settings the SDK would pass. Project
references (`.csproj`/`.vbproj`) are not supported (compile error).

**`src/languages/RunPad.Languages.CSharp`** — the C# language host, on top of `DotnetCommon`:

- `CSharpLanguageHost` — `ApplyKindTransform()` rewrites user code by `RpadFrontMatter.Kind` before
  compiling: `Statements` (top-level statements, passthrough), `Program` (prepends a `Main();` call to the
  user's `void Main()`), `Expression` (wraps the expression and calls `.Dump()` on it).
- `CSharpProjectProfile` — the `RunPad.Runtime` default import and `using X;` syntax;
  `Compiler/CSharpScriptCompiler` adds implicit usings, nullable and the target's C# version.
- `IntelliSense/CSharpIntelliSenseService` — completion, diagnostics, quick fixes, signature help and the
  rest of the editor intelligence, backed by an in-process Roslyn `AdhocWorkspace` kept in sync with the
  editor text. Visual Basic has none yet.
- Compiled scripts run as a child process; stdout and stderr are drained concurrently — draining them one
  after the other deadlocks the child as soon as the other stream's pipe buffer fills.
- `.Dump()` output comes back over stdout with a sentinel prefix (`\x1e` + base64) that
  `CompiledDotnetLanguageHostBase` recognizes and reclassifies as `OutputKind.DumpHtml`.

**`src/languages/RunPad.Languages.VB`** — the Visual Basic host, the same shape as C# without
IntelliSense. Its kind transform swaps the roles of `Statements` and `Program`: VB has no top-level
statements, so `Statements` wraps user code in a generated `Module`/`Sub Main`, while `Program` is the
passthrough.

**`src/runtime/RunPad.Runtime`** — the library referenced by the *user's compiled script*, not by RunPad
itself: `ObjectExtensions.Dump()` and `DumpRenderer` serialize values to the sentinel-prefixed stdout
protocol above. It is MIT-licensed (its own `LICENSE`), unlike the rest of the solution (GPL-3.0), so
compiled scripts can be distributed under any license — never move or copy code from other RunPad
projects into it, and keep it free of dependencies.

**`src/controls`** — reusable Avalonia controls, each its own project: `RunPad.Avalonia.CodeTextEditor`
(the AvaloniaEdit-based editor: breakpoint margin, brace/fold/debug-line highlighting),
`RunPad.Avalonia.HtmlRenderer` (renders `.Dump()` HTML output) and `RunPad.Avalonia.Themes` (Fluent/Simple
theme variants). The controls are language-agnostic: per-language behavior (triggers, brace pairs,
folding, snippets) is supplied through `EditorLanguageProfile`/`ILanguageHostFactory`, never hardcoded in a
control. `AvaloniaEdit` and `AvaloniaEdit.TextMate` are a local fork of the AvaloniaEdit packages,
referenced by `RunPad.Avalonia.CodeTextEditor` and by `RunPad` itself; nullable is disabled in both
(the upstream code predates nullable annotations), and their upstream NUnit suite lives unchanged in
`tests/AvaloniaEdit.Tests`.

**`src/main/RunPad`** — the application shell. `App.axaml.cs` is the composition root (plain
`Microsoft.Extensions.DependencyInjection`) — register new services there. The UI follows MVVM with
**ReactiveUI only** (`ReactiveObject`, `RaiseAndSetIfChanged` or the `field` keyword,
`ReactiveCommand.CreateFromTask` for anything async); do not mix in CommunityToolkit.Mvvm (see
[`MVVM-Guide.md`](MVVM-Guide.md)). Views live in `UserControls`/`Windows`, view models in the matching
`Models` subfolders.

**`src/main/RunPad.Cli`** — the `runpad` console runner: compiles and runs a `.rpad` file the same way
as the GUI's Run button (`ILanguageHost.RunAsync`), without Avalonia or the debugger. `.Dump()` output is
rendered as tables with Spectre.Console (`DumpHtmlSpectreRenderer`). Usage is in the
[user guide](https://campforge.github.io/run-pad-docs/cli/).

## Tests

All RunPad test projects use xunit:

- `tests/RunPad.Runtime.Tests`, `tests/RunPad.Format.RPad.Tests`, `tests/RunPad.Avalonia.HtmlRenderer.Tests`,
  `tests/RunPad.Cli.Tests` — one per project.
- `tests/RunPad.Runtime.UI.Tests` — headless Avalonia UI tests: anything that creates a real `Window` or
  control, or uses `[AvaloniaFact]`/`[AvaloniaTheory]`.
- `tests/RunPad.NonUI.Tests` — language host, IntelliSense, decompiler, DAP and view-model logic that
  never touches a live Avalonia `Application`.

The UI/non-UI split exists because `[assembly: AvaloniaTestFramework]` turns off xunit's collection
parallelism for the *whole* assembly, even for plain `[Fact]`s; keeping Avalonia-independent tests in their
own assembly (with `parallelizeTestCollections` enabled in `xunit.runner.json`) lets them run in parallel.
Both projects reference `RunPad.csproj` and receive the same `InternalsVisibleTo` grants from the
production assemblies — when adding a grant, add it for both.

Projects without a test project of their own (such as `RunPad.Build.Core`) are tested from these two.
`tests/AvaloniaEdit.Tests` is the AvaloniaEdit fork's upstream NUnit suite, kept as it is; RunPad-specific
tests for the fork go to `RunPad.Runtime.UI.Tests`.

## Conventions

- `.editorconfig` enforces CRLF line endings, 4-space indentation, Allman braces, file-scoped namespaces
  and sorted `using` directives outside the namespace. Nullable reference types and implicit usings are
  on solution-wide (`Directory.Build.props`).
- Package versions are managed centrally in `Directory.Packages.props`; a `.csproj` references packages by
  name only, without `Version=`. Add new dependencies there.
- Comments are in English, explain *why*, and stand on their own — no references to issue trackers or
  notes outside this repository.
