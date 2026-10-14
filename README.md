# RunPad

**A native, fast, cross-platform C# scratchpad for .NET** — an open-source alternative to
[LINQPad](https://www.linqpad.net/), [RoslynPad](https://github.com/roslynpad/roslynpad) and
[NetPad](https://github.com/tareqimbasher/NetPad).

- **Native** — a real desktop app built on [Avalonia UI](https://avaloniaui.net/), with no embedded
  browser: even `.Dump()` results are drawn by RunPad's own renderer.
- **Fast** — scripts compile in-process with Roslyn, without MSBuild, and an unchanged script skips the
  build entirely.
- **Cross-platform** — the same app on Windows, Linux and macOS (Apple Silicon and Intel).

Write C# against the .NET SDK you already have, run it in a keystroke and explore the results with
`.Dump()`. Every script is a `.rpad` file — Markdown with a short YAML header and one runnable code
block — so your notes live right next to the code.

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
- **Visual Basic** scripts compile, run and debug too (editor intelligence is C#-only for now).

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
- [Scripts and `.rpad` files](https://campforge.github.io/run-pad-docs/scripts/) — the script format.
- [Roadmap](ROADMAP.md) — what is planned next.

## Building from source

You need the .NET 10 SDK (`global.json` pins the version).

```bash
dotnet build                                         # whole solution
dotnet run --project src/main/RunPad/RunPad.csproj   # start the app
dotnet test                                          # all tests
```

How to send changes, and the rules the code follows, are in [`CONTRIBUTING.md`](CONTRIBUTING.md).

## Contributing

Bug reports, ideas and pull requests are welcome — please read [`CONTRIBUTING.md`](CONTRIBUTING.md)
first. Report problems as [GitHub issues](https://github.com/Campforge/run-pad/issues). Opening a
pull request means you agree to the [Contributor License Agreement](CLA.md).

## AvaloniaEdit fork

RunPad's code editor is built on a **fork of [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit)
and AvaloniaEdit.TextMate**, taken from the 12.0.0 packages and kept in
[`src/controls/AvaloniaEdit`](src/controls/AvaloniaEdit) and
[`src/controls/AvaloniaEdit.TextMate`](src/controls/AvaloniaEdit.TextMate), so the editor control
itself can be changed for RunPad — completion behavior, font rendering, macOS clipboard and
highlighting fixes. The fork keeps its upstream MIT license (`LICENSE` in each folder); its origin
and changes are described in [`src/controls/AvaloniaEdit/README.md`](src/controls/AvaloniaEdit/README.md).

## Acknowledgements

- [netcoredbg](https://github.com/Samsung/netcoredbg) — the debugger, driven over the Debug Adapter
  Protocol.
- [Avalonia UI](https://avaloniaui.net/), and [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit)
  with its predecessor AvalonEdit from SharpDevelop — the editor RunPad's fork is based on.
- [Roslyn](https://github.com/dotnet/roslyn) for compiling and IntelliSense;
  [ILSpy](https://github.com/icsharpcode/ILSpy) for decompiling;
  [Spectre.Console](https://spectreconsole.net/) for the command-line output.
- [Roslynator](https://github.com/dotnet/roslynator) — the idea behind several analyzers.
- [LINQPad](https://www.linqpad.net/), [NetPad](https://github.com/tareqimbasher/NetPad) and
  [RoslynPad](https://github.com/roslynpad/roslynpad) — inspiration.

## License

Copyright (C) 2026 Vadim S ([hobobyte](https://github.com/hobobyte)). RunPad is licensed under the
[GNU General Public License v3.0](LICENSE).

The `RunPad.Runtime` library ([`src/runtime/RunPad.Runtime`](src/runtime/RunPad.Runtime)) — the only
RunPad assembly your scripts are compiled against and shipped with — is licensed under the
[MIT License](src/runtime/RunPad.Runtime/LICENSE), so programs written in RunPad can be distributed
under any license. The installers show an [end-user agreement](installer/EULA.txt) that summarizes
these terms; it adds no restrictions to the GPL-3.0.

Third-party code keeps its own license: the [AvaloniaEdit fork](#avaloniaedit-fork) (MIT) and the
HTML renderer in [`src/controls/RunPad.Avalonia.HtmlRenderer`](src/controls/RunPad.Avalonia.HtmlRenderer)
(BSD-3-Clause and MIT), with license files next to the code. Every third-party component RunPad ships,
with its license text, is listed in [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt).
