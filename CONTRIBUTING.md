# Contributing to RunPad

Thanks for helping. Bug reports, ideas, documentation fixes and code are all welcome.

## Reporting a bug or asking for a feature

Open a [GitHub issue](https://github.com/Campforge/run-pad/issues). For a bug, include:

- your OS, the RunPad version (Help → About RunPad) and `dotnet --list-sdks`;
- what you did, what you expected and what happened instead;
- the smallest `.rpad` script that shows the problem, if there is one.

Check the [roadmap](ROADMAP.md) and the existing issues first — your idea may already be there.

## Before you write code

For anything larger than a small fix, open an issue first and describe the change. It saves you
from building something that does not fit, and lets us agree on the approach before you invest the
time.

## Setting up

You need the .NET 10 SDK (the exact version is pinned in `global.json`). The debugger,
[netcoredbg](https://github.com/Samsung/netcoredbg), is only needed to try debugging; RunPad's setup
window can install it.

```bash
dotnet restore
dotnet build
dotnet run --project src/main/RunPad/RunPad.csproj
```

[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) describes the architecture, the projects and how they depend on each other —
read it before changing anything outside a single class. The `.rpad` format is specified in
[`docs/SpecificationRPad.md`](docs/SpecificationRPad.md).

## Checks

Run the same checks CI runs before you open a pull request:

```bash
dotnet format --verify-no-changes                     # formatting (run `dotnet format` to fix)
dotnet build --configuration Release -warnaserror     # no warnings allowed
dotnet test                                           # all test projects
```

To iterate faster, run one project or one class:

```bash
dotnet test tests/RunPad.NonUI.Tests
dotnet test --filter "FullyQualifiedName~DumpRendererTests"
```

CI runs these on Windows, Linux and macOS.

## Conventions

- **Style** is enforced by `.editorconfig`: CRLF line endings, 4-space indent, Allman braces,
  file-scoped namespaces, sorted `using` directives outside the namespace. Nullable reference types
  are on everywhere.
- **Packages** are versioned centrally in `Directory.Packages.props`; reference them by name only in a
  `.csproj`.
- **Layering**: `src/core` → `src/languages` → `src/main`; `src/controls` stay language-agnostic —
  language-specific behavior is supplied by the language host, never hardcoded in a control.
- **MVVM with ReactiveUI only** — do not mix in CommunityToolkit.Mvvm.
- **`RunPad.Runtime`** is MIT-licensed and ships inside users' programs: keep it free of dependencies
  and never copy code from other RunPad projects into it.
- **Tests**: a bug fix comes with a test that fails without it; a feature comes with tests for its
  behavior. UI tests that need a real Avalonia control go to `tests/RunPad.Runtime.UI.Tests`,
  everything else to `tests/RunPad.NonUI.Tests` or the project's own test project.
- **Comments** are in English and explain *why*; they must make sense on their own, without
  references to issue trackers or notes outside this repository.

## Pull requests

- One topic per pull request; keep unrelated refactoring out.
- Commit messages: a short imperative subject (`Fix …`, `Add …`), then a body explaining what changed
  and why when it is not obvious.
- Describe how you tested the change; for UI changes, a screenshot helps.
- If the change is visible to users, mention it so the
  [user guide](https://github.com/Campforge/run-pad-docs) can be updated — or send a pull request
  there too.

## License of contributions

By contributing, you agree that your contribution is licensed under the license of the code you
change: the [GNU GPL v3.0](LICENSE) for RunPad, and the [MIT License](src/runtime/RunPad.Runtime/LICENSE)
for `src/runtime/RunPad.Runtime`. Only submit work you have the right to contribute.
