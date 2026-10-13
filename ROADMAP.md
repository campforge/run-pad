# Roadmap

Where RunPad is heading. This is a direction, not a schedule: items have no dates and the order
within a section is not a promise. If something here matters to you — or something is missing —
open an [issue](https://github.com/Campforge/run-pad/issues) and say so; that is the best way to
move it up.

## Next

- **Visual Basic editor intelligence** — completion, signature help, live diagnostics and quick fixes
  for VB, which today only compiles, runs and debugs.
- **File → Open** — open a `.rpad` file from anywhere, not only from the scripts folder.
- **Set Next Statement** in the debugger.
- **Signed macOS builds** — Developer ID signing and notarization, so macOS no longer asks to
  confirm the first launch.

## Editor

- Format on type and Format Selection (Format Document already exists).
- Completion polish: soft selection, recently used items first, per-item commit characters,
  target-typed preselection and full fuzzy matching.
- CodeLens and a syntax tree viewer.

## Scripts and results

- Project references (`.csproj`/`.vbproj`) — today a script references a built `.dll` instead.
- Exporting results (HTML, Excel).
- Database connections for querying data from scripts.

## Languages

- **Languages as real plugins** — language support loaded at runtime instead of compiled into RunPad.
  Some groundwork is in place: every language already plugs in through the same host interface.
- **New languages** — F#, SQL (starting with in-memory SQLite), PowerShell, Python, Go, C, C++ and
  Rust have been looked at; nothing is started yet.

## Internals

- Fold the remaining duplication between the C# and Visual Basic back ends.
- Thread safety of the build cache and better testability of the process-running code.

## Done recently

See the [releases page](https://github.com/Campforge/run-pad/releases) for what each version
shipped, and the [user guide](https://campforge.github.io/run-pad-docs/) for everything RunPad
can do today.
