# Contributing

Thanks for helping. Bug reports, ideas, documentation fixes and code are all welcome. Report bugs and
ideas as [GitHub issues](https://github.com/campforge/run-pad/issues) — for a bug, include your OS,
the RunPad version (Help → About RunPad), `dotnet --list-sdks` and the smallest `.rpad` script that
shows the problem. For anything larger than a small fix, open an issue first and agree on the approach.

## Getting started

You need the .NET 10 SDK (pinned in `global.json`). Debugging also needs
[netcoredbg](https://github.com/Samsung/netcoredbg); RunPad's setup window can install it.

```bash
dotnet build
dotnet run --project src/main/RunPad/RunPad.csproj
```

Before opening a pull request, run what CI runs (on Windows, Linux and macOS):

```bash
dotnet format --verify-no-changes                  # `dotnet format` fixes it
dotnet build --configuration Release -warnaserror
dotnet test                                        # or one project: dotnet test tests/RunPad.NonUI.Tests
```

## Contributor License Agreement

By opening a pull request you agree to the [Contributor License Agreement](CLA.md) — nothing to sign.
You keep the copyright in your work and grant the maintainer a license to distribute it as part of
RunPad, under the project's license and, if the licensing ever changes, under other terms.

## Rules

- **Language:** C# on .NET 10 only, with Avalonia UI for the interface. No web views or embedded
  browsers — RunPad is a native app.
- **License:** contributions are GPL-3.0 ([`LICENSE`](LICENSE)); `src/runtime/RunPad.Runtime` is MIT
  ([its `LICENSE`](src/runtime/RunPad.Runtime/LICENSE)) and must stay free of dependencies and of code
  copied from other RunPad projects, because it ships inside users' programs.
- **Clean-room:** do not copy code, icons or other assets from proprietary software, LINQPad included.
  Matching a public API shape for compatibility is fine; the implementation must be your own, from
  documentation and observed behavior. Third-party code or assets need a permissive license compatible
  with GPL-3.0, keep that license next to them, and credit the source in the file (as the analyzers
  ported from Roslynator ideas do).
- **Never crash:** a broken script, bad input or a failing tool (SDK, NuGet, debugger) must never take
  RunPad down. Report the error in the output or a dialog, catch at every `async void` boundary, and
  ship a regression test with every crash fix.
- **Layering:** `src/core` → `src/languages` → `src/main`; controls in `src/controls` stay
  language-agnostic — per-language behavior comes from the language host, never from a control.
- **AvaloniaEdit fork:** `src/controls/AvaloniaEdit` and `src/controls/AvaloniaEdit.TextMate` are a
  fork of AvaloniaEdit under its own MIT license (see
  [their README](src/controls/AvaloniaEdit/README.md)). Change them only when the control itself has to
  change — otherwise use `RunPad.Avalonia.CodeTextEditor`; keep upstream's style and license headers,
  and leave the upstream suite in `tests/AvaloniaEdit.Tests` as it is (RunPad's tests for the fork go
  to `tests/RunPad.Runtime.UI.Tests`).
- **MVVM:** ReactiveUI only (no CommunityToolkit.Mvvm); register services in `App.axaml.cs`.
- **Packages:** versions live in `Directory.Packages.props`; a `.csproj` references packages by name only.
  A new package must have a license compatible with GPL-3.0; after adding, removing or updating one,
  regenerate `THIRD-PARTY-NOTICES.txt` with `python scripts/make-third-party-notices.py` and commit it.
- **Tests:** required for every change. UI tests that create real Avalonia controls go to
  `tests/RunPad.Runtime.UI.Tests`, everything else to `tests/RunPad.NonUI.Tests` or the project's own
  test project.
- **Style:** `.editorconfig` (CRLF, 4 spaces, Allman braces, file-scoped namespaces) and no warnings.
  Match the surrounding code. Comments are in English, explain *why* and stand on their own — no
  references to issue numbers or notes outside the repository.
- **UI:** use the theme resources from `RunPad.Avalonia.Themes` instead of hard-coded colors, check
  both the light and dark themes, and attach before/after screenshots to the pull request.
- **Commits:** small and focused, with a short imperative subject (`Fix …`, `Add …`); one topic per
  pull request.
- **Docs:** if users can see the change, update the [user guide](https://github.com/campforge/run-pad-docs)
  too or mention it in the pull request.
- **Changelog:** if users can see the change, add a line for them under `## [Unreleased]` in
  [`CHANGELOG.md`](CHANGELOG.md) — it becomes the release notes of the next version.

## Adding an analyzer

RunPad ships its own C# analyzers (`RPADxxxx`), shown as squiggles while typing and next to the
lightbulb.

1. **Check it is new.** Search `src/languages/RunPad.Languages.CSharp/IntelliSense/Analyzers/` and the
   built-in Roslyn analyzers — extend an existing analyzer rather than add a near-duplicate.
2. **Write the analyzer** as one file in that folder: a `[DiagnosticAnalyzer(LanguageNames.CSharp)]`
   class with a public `DiagnosticId` constant — the next free `RPADxxxx` number — and a
   `DiagnosticDescriptor` with a title, message and description. Register only the syntax or operation
   kinds you need, call `EnableConcurrentExecution()` and skip generated code. RunPad discovers it by
   reflection; there is no list to update.
3. **Keep false positives near zero.** Use the semantic model rather than names alone, and when the
   rule is "this value always throws", confirm it against the real .NET API.
4. **Credit the source** in a comment at the top if the idea comes from another project.
5. **Unit tests** in `tests/RunPad.NonUI.Tests/RunPadAnalyzersTests.cs`: cases that must be flagged
   and cases that must not be.
6. **End-to-end test** in `tests/RunPad.NonUI.Tests/CSharpIntelliSenseAnalyzersTests.cs` proving the
   diagnostic reaches the editor through the real IntelliSense pipeline.
