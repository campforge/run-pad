---
title: Scripts and .rpad files
description: The .rpad file format, front matter fields and the Statements, Program and Expression kinds.
---

# Scripts and .rpad files

A RunPad script is a plain-text `.rpad` file. It is a Markdown document with:

1. a YAML **front matter** block between two `---` lines at the very top;
2. exactly one **run block**: a fenced code block whose info string is `csharp run` or
   `vb run`, matching the script's language;
3. any other Markdown you like, before or after the run block.

````markdown
---
version: "1.0"
language: csharp
targetSdk: net10.0
kind: Statements
---

# Monthly totals

Notes about what this script does.

```csharp run
var total = new[] { 120, 80, 45 }.Sum();
total.Dump("Total");
```

More notes after the code.
````

Because the file is Markdown, it reads well on GitHub and in any Markdown editor, and it
diffs cleanly in version control.

## How RunPad edits the file

The editor shows only the code inside the run block. RunPad keeps the Markdown before
and after the block exactly as it was and writes it back unchanged when it saves. The
front matter is changed through the UI: the **kind** drop-down in the toolbar, the
[Dependencies window](dependencies.md) and code fixes that add a namespace. To edit the
Markdown text around the code, use another text editor.

When RunPad saves a script, it writes the front matter again from the fields it knows,
in a fixed order. YAML comments and unknown fields in the header are not kept.

## Front matter

All fields are optional. A missing or empty value falls back to the default.

| Field | Default | Meaning |
| --- | --- | --- |
| `version` | `"1.0"` | Version of the file format. |
| `language` | `csharp` | `csharp` or `vb`. Must match the run block's info string. |
| `targetSdk` | `latest` | A framework moniker such as `net10.0`. Recorded in the file and shown in the status bar as *Kind · targetSdk*. It does **not** select the SDK: the GUI uses the SDK chosen in the toolbar drop-down, and the [command-line runner](cli.md) uses the newest stable installed SDK. |
| `kind` | `Statements` | `Statements`, `Program` or `Expression`. See [Kinds](#kinds). |
| `packages` | none | NuGet packages as `PackageId: version` pairs. |
| `references` | none | Paths to `.dll` files. See [Packages and references](dependencies.md). |
| `namespaces` | none | Namespaces imported for the whole script, like global `using` (C#) or project-level `Imports` (VB). |

Field names are case-sensitive camelCase (`targetSdk`, not `TargetSdk`). The values of
`language` and `kind` are case-insensitive. Unknown fields are ignored when the file is
read.

A complete header:

```yaml
---
version: "1.0"
language: csharp
targetSdk: net10.0
kind: Statements
packages:
  Humanizer.Core: 2.14.1
references:
  - $(CurrentQueryPath)/lib/MyHelpers.dll
namespaces:
  - System.Text.Json
  - System.Globalization
---
```

!!! warning "Invalid files are opened as plain text"
    RunPad refuses to run a file that has no front matter, an unclosed front matter
    block, invalid YAML (for example `kind: Script`), or no run block for the declared
    language. The tab shows the whole file as text, the Results tab explains the
    problem, and the status strip says *Invalid document*. Fix the file and reopen it.

### The run block

The opening fence must be exactly three backticks, the language id, whitespace, and
`run` (for example `` ```csharp run ``). Variations such as `csharp runner` or
`csharprun` are not run blocks. RunPad uses the first matching block in the file. Other
fenced blocks, such as `csharp` examples without `run`, are ordinary Markdown.

### Namespaces imported by default

C# scripts get the namespaces of the .NET SDK's implicit usings plus RunPad's runtime
namespace: `System`, `System.Collections.Generic`, `System.IO`, `System.Linq`,
`System.Net.Http`, `System.Threading`, `System.Threading.Tasks` and `RunPad.Runtime`.
Visual Basic scripts import the same list. Add anything else to `namespaces`, or let a
[quick fix](analyzers.md#quick-fixes-and-the-lightbulb) add it for you.

C# scripts compile with nullable reference types enabled and the default C# language
version of the selected SDK (for example C# 14 for .NET 10).

## Kinds

The kind decides how RunPad turns your code into a program. Change it with the kind
drop-down in the toolbar; the choice is saved to the front matter.

### Statements

Top-level statements, like a modern C# `Program.cs`. You can declare local functions
and types after the statements.

```csharp
var words = new[] { "alpha", "beta", "gamma" };
words.Select(w => w.ToUpperInvariant()).Dump();
```

In Visual Basic, RunPad wraps the code in a module with a `Sub Main`, so you write only
the statements:

```vb
Dim names = {"Ada", "Grace", "Linus"}
For Each name In names
    Console.WriteLine($"Hello, {name}!")
Next
names.Select(Function(n) New With {.Name = n, .Letters = n.Length}).Dump("Names")
```

### Program

You write a `Main` method and whatever else it needs: fields, helper methods, nested
types. In C#, RunPad places your code inside a generated class and calls `Main` for you.
`Main` may be `void` or return `Task` (`async Task Main()`), and it may be static or
not.

```csharp
void Main()
{
    Console.WriteLine(Greet("RunPad"));
}

string Greet(string name) => $"Hello, {name}!";
```

In Visual Basic the code is compiled as written, so a Program script contains the whole
module:

```vb
Module Program
    Sub Main()
        Console.WriteLine(Twice(21))
    End Sub

    Function Twice(value As Integer) As Integer
        Return value * 2
    End Function
End Module
```

### Expression

The whole block is one expression, written without a trailing semicolon. RunPad
evaluates it and renders the result as if you had called `.Dump()` on it.

```csharp
Enumerable.Range(1, 10).Where(n => n % 2 == 0)
```

The [examples](examples.md) include one script of each C# kind.

## Where your scripts live

RunPad shows the `.rpad` files and folders inside **RunPad Queries** in your Documents
folder (created on first start) as the file tree on the left. The tree refreshes when
files change on disk. Right-click it to create C# or Visual Basic files and folders,
rename, delete, or *Show in File Manager*; drag items to move them.

There is no File → Open command yet. To use a script from elsewhere, copy it into the
RunPad Queries folder. LINQPad `.linq` files can be imported with
**File → Import LINQPad Query...**; see [Command line and LINQPad import](cli.md#importing-linqpad-queries).
