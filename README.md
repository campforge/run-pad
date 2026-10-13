# RunPad

A desktop scratchpad for C# and Visual Basic scripts — in the same space as LINQPad, RoslynPad and
NetPad, cross-platform (Windows, Linux, macOS) and open source.

Every script is a `.rpad` file: a Markdown document with a short YAML header and one runnable code
block, so a script can carry its own notes. RunPad compiles the block with the .NET SDK on your
machine, runs it and shows the output and `.Dump()` results right under the editor.

![RunPad main window](https://campforge.github.io/run-pad-docs/assets/screenshots/overview.png)

## Features

- **Three script kinds** — top-level statements, a `Main` program or a single expression.
- **C# IntelliSense** — completion with automatic imports, signature help, quick info, semantic
  highlighting, rename, Find References, Go to Definition, formatting.
- **Live diagnostics** — compiler errors, Roslyn analyzers, RunPad's own analyzers and lightbulb
  quick fixes as you type.
- **Debugger** — breakpoints with conditions and hit counts, stepping, locals, watches, call stack,
  threads and exception breaks (via [netcoredbg](https://github.com/Samsung/netcoredbg)).
- **Rich results** — `.Dump()` tables, live-updating `DumpContainer`, progress bars, interactive input.
- **NuGet packages and references** from a Dependencies window; **Assembly Explorer** with decompiled
  C#, IL, resources and usage analysis.
- **Command-line runner** for scripts and a **LINQPad `.linq` importer**.
- **Visual Basic** compiles, runs and debugs the same way (editor intelligence is C#-only for now).

## Download

Installers and portable builds are on the [releases page](https://github.com/Campforge/run-pad/releases/latest):

| OS | Package |
| --- | --- |
| Windows | `RunPad-win-x64.msi`, or the portable `RunPad-win-x64.zip` |
| Linux | `runpad_<version>_amd64.deb`, `RunPad-x86_64.AppImage` or `RunPad-linux-x64.tar.gz` |
| macOS | `RunPad-osx-arm64.dmg` (Apple Silicon) or `RunPad-osx-x64.dmg` (Intel) |

RunPad needs the [.NET SDK](https://dotnet.microsoft.com/download) to compile scripts. The macOS build
is not notarized yet — see the [installation guide](https://campforge.github.io/run-pad-docs/install/)
for the first-launch steps on each OS.

## Documentation

- [User guide](https://campforge.github.io/run-pad-docs/) — installation, quick start, editor,
  debugging, results, packages, command line.
- [`.rpad` format specification](docs/SpecificationRPad.md).
- [Roadmap](ROADMAP.md) — what is planned next.

## Building from source

You need the .NET 10 SDK (`global.json` pins the version).

```bash
dotnet build                                         # whole solution
dotnet run --project src/main/RunPad/RunPad.csproj   # start the app
dotnet test                                          # all tests
```

Architecture, project layout and coding conventions are in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md); how to send
changes is in [`CONTRIBUTING.md`](CONTRIBUTING.md).

## Contributing

Bug reports, ideas and pull requests are welcome — please read [`CONTRIBUTING.md`](CONTRIBUTING.md)
first. Report problems as [GitHub issues](https://github.com/Campforge/run-pad/issues).

## Acknowledgements

- [netcoredbg](https://github.com/Samsung/netcoredbg) — the debugger, driven over the Debug Adapter
  Protocol.
- [Avalonia UI](https://avaloniaui.net/) and [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit)
  (a local fork lives in `src/controls`).
- [Roslyn](https://github.com/dotnet/roslyn) for compiling and IntelliSense;
  [ILSpy](https://github.com/icsharpcode/ILSpy) for decompiling;
  [Spectre.Console](https://spectreconsole.net/) for the command-line output.
- [Roslynator](https://github.com/dotnet/roslynator) — the idea behind several analyzers.
- [LINQPad](https://www.linqpad.net/), [NetPad](https://github.com/tareqimbasher/NetPad) and
  [RoslynPad](https://github.com/roslynpad/roslynpad) — inspiration.

## License

Copyright (C) 2026 Vadim ([hobobyte](https://github.com/hobobyte)). RunPad is licensed under the
[GNU General Public License v3.0](LICENSE).

The `RunPad.Runtime` library ([`src/runtime/RunPad.Runtime`](src/runtime/RunPad.Runtime)) — the only
RunPad assembly your scripts are compiled against and shipped with — is licensed under the
[MIT License](src/runtime/RunPad.Runtime/LICENSE), so programs written in RunPad can be distributed
under any license. The installers show an [end-user agreement](installer/EULA.txt) that summarizes
these terms; it adds no restrictions to the GPL-3.0.
