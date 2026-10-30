# Invoklet

**A native, fast, cross-platform C# scratchpad for .NET** — free and open source.

- **Native** — a real desktop app built on [Avalonia UI](https://avaloniaui.net/), with no embedded
  browser: even `.Dump()` results are drawn by Invoklet's own renderer.
- **Fast** — scripts compile in-process with Roslyn, without MSBuild, and an unchanged script skips the
  build entirely.
- **Cross-platform** — the same app on Windows, Linux and macOS (Apple Silicon and Intel).

Write C# against the .NET SDK you already have, run it in a keystroke and explore the results with
`.Dump()`. Every script is a `.inv` file — Markdown with a short YAML header and one runnable code
block — so your notes live right next to the code.

![Invoklet main window](https://tervforge.github.io/invoklet-docs/assets/screenshots/overview.png)

## Features

- **Three script kinds** — top-level statements, a `Main` program or a single expression.
- **C# IntelliSense** — completion with automatic imports, signature help, quick info, semantic
  highlighting, rename, Find References, Go to Definition, formatting.
- **Live diagnostics** — compiler errors, Roslyn analyzers, Invoklet's own analyzers and lightbulb
  quick fixes as you type.
- **Debugger** — breakpoints with conditions and hit counts, stepping, locals, watches, call stack,
  threads and exception breaks (via [netcoredbg](https://github.com/Samsung/netcoredbg)).
- **Rich results** — `.Dump()` tables, live-updating `DumpContainer`, progress bars, interactive input.
- **NuGet packages and references** from a Dependencies window; **Assembly Explorer** with decompiled
  C#, IL, resources and usage analysis.
- **Command-line runner** for scripts and a **`.linq` query importer**.
- **Visual Basic** scripts compile, run and debug too (editor intelligence is C#-only for now).

## Download

Installers and portable builds are on the [releases page](https://github.com/tervforge/invoklet/releases/latest):

| OS | Package |
| --- | --- |
| Windows | `Invoklet-win-x64.msi`, or the portable `Invoklet-win-x64.zip` |
| Linux | `invoklet_<version>_amd64.deb`, `Invoklet-x86_64.AppImage` or `Invoklet-linux-x64.tar.gz` |
| macOS | `Invoklet-osx-arm64.dmg` (Apple Silicon) or `Invoklet-osx-x64.dmg` (Intel) |

Invoklet needs the [.NET SDK](https://dotnet.microsoft.com/download) to compile scripts. The macOS build
is not notarized yet — see the [installation guide](https://tervforge.github.io/invoklet-docs/install/)
for the first-launch steps on each OS.

## Documentation

- [User guide](https://tervforge.github.io/invoklet-docs/) — installation, quick start, editor,
  debugging, results, packages, command line.
- [Scripts and `.inv` files](https://tervforge.github.io/invoklet-docs/scripts/) — the script format.
- [Changelog](CHANGELOG.md) — what changed in each release.
- [Roadmap](ROADMAP.md) — what is planned next.

## Building from source

You need the .NET 10 SDK (`global.json` pins the version).

```bash
dotnet build                                         # whole solution
dotnet run --project src/main/Invoklet/Invoklet.csproj   # start the app
dotnet test                                          # all tests
```

How to send changes, and the rules the code follows, are in [`CONTRIBUTING.md`](CONTRIBUTING.md).

## Contributing

Bug reports, ideas and pull requests are welcome — please read [`CONTRIBUTING.md`](CONTRIBUTING.md)
first. Report problems as [GitHub issues](https://github.com/tervforge/invoklet/issues). Opening a
pull request means you agree to the [Contributor License Agreement](CLA.md).

## AvaloniaEdit fork

Invoklet's code editor is built on a **fork of [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit)
and AvaloniaEdit.TextMate**, taken from the 12.0.0 packages and kept in
[`src/controls/AvaloniaEdit`](src/controls/AvaloniaEdit) and
[`src/controls/AvaloniaEdit.TextMate`](src/controls/AvaloniaEdit.TextMate), so the editor control
itself can be changed for Invoklet — completion behavior, font rendering, macOS clipboard and
highlighting fixes. The fork keeps its upstream MIT license (`LICENSE` in each folder); its origin
and changes are described in [`src/controls/AvaloniaEdit/README.md`](src/controls/AvaloniaEdit/README.md).

## MarkView.Avalonia fork

A **fork of [MarkView.Avalonia](https://github.com/Kryptos-FR/MarkView.Avalonia)** and its
SyntaxHighlighting extension, taken from the 12.3.0 release, is kept in
[`src/controls/MarkView.Avalonia`](src/controls/MarkView.Avalonia) and
[`src/controls/MarkView.Avalonia.SyntaxHighlighting`](src/controls/MarkView.Avalonia.SyntaxHighlighting)
for the planned Markdown preview of scripts. It keeps its upstream MIT license (`LICENSE.md` in each
folder); its origin and changes are described in
[`src/controls/MarkView.Avalonia/README.md`](src/controls/MarkView.Avalonia/README.md).

## Acknowledgements

- [netcoredbg](https://github.com/Samsung/netcoredbg) — the debugger, driven over the Debug Adapter
  Protocol.
- [Avalonia UI](https://avaloniaui.net/), and [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit)
  with its predecessor AvalonEdit from SharpDevelop — the editor Invoklet's fork is based on.
- [MarkView.Avalonia](https://github.com/Kryptos-FR/MarkView.Avalonia) and
  [Markdig](https://github.com/xoofx/markdig) — the Markdown viewer Invoklet's fork is based on.
- [Roslyn](https://github.com/dotnet/roslyn) for compiling and IntelliSense;
  [ILSpy](https://github.com/icsharpcode/ILSpy) for decompiling;
  [Spectre.Console](https://spectreconsole.net/) for the command-line output.
- [Roslynator](https://github.com/dotnet/roslynator) — the idea behind several analyzers.
- [LINQPad](https://www.linqpad.net/), [NetPad](https://github.com/tareqimbasher/NetPad) and
  [RoslynPad](https://github.com/roslynpad/roslynpad) — inspiration.

## License

Copyright (C) 2026 Vadim S ([svncore](https://github.com/svncore), <svnpub@proton.me>). Invoklet is licensed under the
[GNU General Public License v3.0](LICENSE).

The `Invoklet.Runtime` library ([`src/runtime/Invoklet.Runtime`](src/runtime/Invoklet.Runtime)) — the only
Invoklet assembly your scripts are compiled against and shipped with — is licensed under the
[MIT License](src/runtime/Invoklet.Runtime/LICENSE), so programs written in Invoklet can be distributed
under any license. The installers show an [end-user agreement](installer/EULA.txt) that summarizes
these terms; it adds no restrictions to the GPL-3.0.

Third-party code keeps its own license: the [AvaloniaEdit fork](#avaloniaedit-fork) (MIT), the
[MarkView.Avalonia fork](#markviewavalonia-fork) (MIT) and the HTML renderer in [`src/controls/Invoklet.Avalonia.HtmlRenderer`](src/controls/Invoklet.Avalonia.HtmlRenderer)
(BSD-3-Clause and MIT), with license files next to the code. Every third-party component Invoklet ships,
with its license text, is listed in [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt).
