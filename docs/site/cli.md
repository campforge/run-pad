---
title: Command line and LINQPad import
description: Run .rpad scripts from a terminal with the runpad CLI and convert LINQPad .linq queries.
---

# Command line and LINQPad import

## The `runpad` command-line runner

`runpad` compiles and runs the code block of a `.rpad` file in a terminal, without the
RunPad window and without the debugger. It is useful for automation and CI. It runs C#
and Visual Basic scripts and honors `kind`, `packages`, `references` (including the path
macros) and `namespaces` the same way the RunPad window does.

!!! info "Getting the runner"
    The runner is not included in the release packages. On Linux, the `runpad` command
    that the `.deb` package puts on `PATH` starts the RunPad window, not this runner.

    The runner is the `RunPad.Cli` project in the RunPad source repository. Build it from
    a clone with the .NET 10 SDK:

    ```bash
    dotnet build src/main/RunPad.Cli -c Release
    dotnet src/main/RunPad.Cli/bin/Release/net10.0/runpad.dll path/to/script.rpad
    ```

    Or run it straight from source:

    ```bash
    dotnet run --project src/main/RunPad.Cli -- path/to/script.rpad
    ```

    The build also produces a native launcher, `runpad` (`runpad.exe` on Windows), that
    works without the `dotnet` prefix if it can find the .NET runtime. With a
    non-standard .NET install, for example one from Homebrew or Linuxbrew, it may not, so
    use the `dotnet …/runpad.dll` form, which always works.

### Usage

```text
runpad <path-to.rpad>
runpad convert <input.linq> [output.rpad]
```

Running with no arguments prints this usage and exits with code 1.

- Script output goes to standard output. Text written to `Console.Error` and compiler
  errors go to standard error.
- `.Dump()` output is rendered in the terminal as tables, with nested tables for nested
  values and colors by value type. Colors are used only when the output is a terminal,
  not when it is redirected to a file or pipe.
- `DumpContainer` and `Util.ProgressBar` updates are printed one after another instead of
  updating in place.
- The script runs with the newest stable .NET SDK found by `dotnet --list-sdks` on
  `PATH`; the `targetSdk` field does not change this.
- Each run compiles into a new temporary folder that is deleted afterwards. The runner
  does not use the RunPad window's build cache.
- NuGet sources come from RunPad's own `NuGet.Config` if the RunPad window has created
  one; otherwise your normal NuGet settings are used.
- [Interactive input](results.md#interactive-input) is not supported. A script that calls
  `Util.ReadLine` waits until you press ++ctrl+c++.

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | The runner finished without reporting a compiler error or standard-error output. |
| `1` | Bad arguments, file not found, the file is not a valid `.rpad` document, no .NET SDK found, a compile error, or the script wrote to standard error (an unhandled exception does). |
| `130` | Interrupted with ++ctrl+c++. |

The runner currently does **not** forward the script process's numeric exit code. A script that calls `Environment.Exit(42)` without writing to standard error can therefore leave the runner with exit code `0`. Use the reported diagnostics and output when assessing a run.

### Example

```console
$ dotnet runpad.dll debugging-loop.rpad
i = 1, square = 1, running total = 1
i = 2, square = 4, running total = 5
i = 3, square = 9, running total = 14
i = 4, square = 16, running total = 30
i = 5, square = 25, running total = 55
Sum of squares 1..5
55
```

## Importing LINQPad queries

RunPad converts LINQPad `.linq` files to `.rpad` scripts.

- **In RunPad:** choose **File → Import LINQPad Query...** and pick the `.linq` file.
  RunPad writes a `.rpad` file with the same name **next to the `.linq` file** and opens
  it. If that name is taken, it adds a number (`Query 2.rpad`). The file appears in the
  file tree only if it is inside your RunPad Queries folder.
- **From a terminal:** `runpad convert query.linq` writes `query.rpad` next to the input.
  Give a second argument to choose the output path. The command **overwrites** an
  existing output file.

### What is converted

| LINQPad | RunPad |
| --- | --- |
| `Kind="Statements"`, `"Program"`, `"Expression"` | `language: csharp` with the same kind |
| `Kind="VBStatements"`, `"VBProgram"`, `"VBExpression"` | `language: vb` with the same kind |
| `<NuGetReference Version="…">Id</NuGetReference>` | `packages:` entry (`latest` when no version is given) |
| `<Namespace>` | `namespaces:` entry |
| `<Reference>` (or its `Relative` attribute) | `references:` entry |
| The code after `</Query>` | The run block, under a heading with the file name |

Other kinds, such as SQL or F# queries, are rejected with an error that lists the
supported kinds. LINQPad database connections are not converted. The converted file
gets `targetSdk: latest`.

Most `.Dump()` calls work unchanged because RunPad's `.Dump()` overloads mirror
LINQPad's; see [Results and runtime helpers](results.md). LINQPad's long positional
`Dump(description, depth, collapseTo, toDataGrid, …)` overloads are not provided; use
`Dump(new DumpOptions { … })` instead.

### Try it

Download [`linqpad-sample.linq`](examples/linqpad-sample.linq):

```xml
<Query Kind="Statements">
  <Namespace>System.Globalization</Namespace>
</Query>

var culture = CultureInfo.InvariantCulture;
var prices = new[] { 9.99m, 24.50m, 3.25m };

prices
    .Select(p => new { Price = p, Text = p.ToString("N2", culture) })
    .Dump("Formatted prices");
```

`runpad convert linqpad-sample.linq` produces:

````markdown
---
version: 1.0
language: csharp
targetSdk: latest
kind: Statements
namespaces:
  - System.Globalization
---

# linqpad-sample

```csharp run
var culture = CultureInfo.InvariantCulture;
var prices = new[] { 9.99m, 24.50m, 3.25m };

prices
    .Select(p => new { Price = p, Text = p.ToString("N2", culture) })
    .Dump("Formatted prices");
```
````

Running the converted script shows a **Formatted prices** table with the prices and the
texts `"9.99"`, `"24.50"` and `"3.25"`.
