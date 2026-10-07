# RPad File Format Specification (Draft v1)

## Overview

A file with the **`.rpad`** extension is a plain Markdown document containing:

* document metadata (YAML front matter);
* one or more source code blocks;
* arbitrary documentation in Markdown.

The main goals of the format:

* fully text-based;
* easy for humans to read;
* renders well on GitHub/GitLab;
* works well with version control systems;
* allows manual editing;
* easy to extend without breaking compatibility.

The format is based on two existing standards:

* Markdown (CommonMark)
* YAML Front Matter

---

# Document structure

A document consists of two parts:

1. YAML front matter
2. Markdown content

Example:

````markdown
---
version: "1.0"
language: csharp
targetSdk: net9.0
kind: Statements
---

# Example

Description of the example.

## Program

```csharp run
Console.WriteLine("Hello");
```
````

---

# YAML Front Matter

Metadata is located at the beginning of the file between two `---` lines.

All parameters are global to the document.

## version

The file format version.

Type: string.

```yaml
version: "1.0"
```

Required.

Used to determine the document schema.

---

## language

The programming language.

Type:

```yaml
language: csharp
```

Required.

Supported values in version 1:

* `csharp`

The following may be added in the future:

* `java`
* `kotlin`
* `python`
* `javascript`
* `go`
* `rust`

---

## targetSdk

The target .NET version.

Type:

```yaml
targetSdk: net9.0
```

Required. If the key is missing or has no value, the editor does not raise
an error but falls back to the default value `latest`.

Examples:

```yaml
targetSdk: net8.0
```

```yaml
targetSdk: net9.0
```

```yaml
targetSdk: latest
```

> The key was renamed from `targetFramework` to `targetSdk` (2026-07-06). Files created before that
> date must be fixed manually — there is no automatic migration.

---

## kind

The type of the document's executable block. Determines how the editor interprets and compiles the code.

Type:

```yaml
kind: Statements
```

Required.

Supported values:

| Value          | Purpose                                                  |
| -------------- | -------------------------------------------------------- |
| `Statements`   | Top-level statements (default)                           |
| `Program`      | A full program with an explicit `void Main()`            |
| `Expression`   | A single expression; the result is shown via `.Dump()`   |

---

### Statements

The code runs as C# top-level statements. Allowed:
- method calls and statements;
- inline method declarations (local functions);
- class, struct, and enum declarations.

Default template when creating a new file:

```csharp
Console.WriteLine("Hello, RPad!");
```

---

### Program

The user explicitly declares a `void Main()` entry point. The editor automatically adds a call to `Main()` when compiling.

Default template when creating a new file:

```csharp
void Main()
{

}

// You can define other methods, fields, classes and namespaces here
```

---

### Expression

A single C# expression without a trailing semicolon. The editor wraps the result in `.Dump()` for output.

Example:

```csharp
DateTime.Now
```

The editor compiles this as:

```csharp
(
DateTime.Now
).Dump();
```

---

## references

A list of project or assembly references.

Type:

```yaml
references:
  - ../Shared/Shared.csproj
  - ../Libraries/Utilities.dll
```

Optional.

Supported:

* projects (`.csproj`, `.vbproj`)
* assemblies (`.dll`)

Paths may use macros (in the spirit of LINQPad):

* `$(RuntimeDirectory)` — the directory of the installed .NET runtime.
* `$(MyDocuments)` — the current user's "My Documents" directory.
* `$(CurrentQueryPath)` — the directory containing the current `.rpad` file (not the file itself but its
  parent folder — handy for paths like `$(CurrentQueryPath)/lib/Utilities.dll`). For an
  unsaved tab (no path of its own) this macro is not expanded and stays in its
  original form — the reference will not be found at build time, producing a clear error instead of
  unexpected behavior.

```yaml
references:
  - $(CurrentQueryPath)/lib/Utilities.dll
  - $(MyDocuments)/Shared/Shared.csproj
```

---

## packages

NuGet packages.

Type:

```yaml
packages:
  Dapper: 2.1.66
  Newtonsoft.Json: 13.0.4
```

Optional.

The key is the package identifier.

The value is the version.

---

## namespaces

A list of global usings.

Type:

```yaml
namespaces:
  - System.Text
  - System.Linq
  - Microsoft.Extensions.Logging
```

Optional.

The editor may add them automatically when compiling.

> The key was renamed from `usings` to `namespaces` (2026-07-09). Files created before that date
> must be fixed manually — there is no automatic migration.

---

# Markdown

Plain Markdown follows the YAML.

It may contain:

* headings;
* lists;
* images;
* links;
* descriptions;
* any other valid Markdown.

The editor does not modify Markdown that does not belong to the special blocks.

---

# The Program block

Executable code is placed inside a fenced code block.

Minimal example:

````markdown
## Program

```csharp run
Console.WriteLine("Hello World");
```
````

The block's language must match the `language` value.

The first version supports:

```
csharp
vb
```

`vb` (Visual Basic) is compiled, run, and debugged the same way as `csharp`, but for now without
IntelliSense (autocompletion/diagnostics/quick fixes) — see `RunPad.Languages.VB`.

---

# The run attribute

The `run` keyword marks the block as executable.

Example:

````markdown
```csharp run
Console.WriteLine("Hello");
```
````

A document may contain only one `run` block.

---

# Full example

````markdown
---
version: "1.0"
language: csharp
targetSdk: net9.0
kind: Statements

packages:
  Dapper: 2.1.66
  Microsoft.Data.SqlClient: 6.1.2

references:
  - ../Shared/Shared.csproj

namespaces:
  - System.Text
  - System.Linq
---

# SQL Example

Demonstrates using Dapper.

## Program

```csharp run
using Dapper;

Console.WriteLine("Hello RPad!");
```
````

---

# Format requirements

The file must be saved as:

* UTF-8
* LF or CRLF

---

# Compatibility

Every `.rpad` file is a valid Markdown document.

When the file is opened:

* in Visual Studio Code;
* in Obsidian;
* on GitHub;
* in GitLab;

it must render as ordinary Markdown with C# syntax highlighting.

Interpreting the special features (`run`, YAML, etc.) requires RPad format support.

---

# Format evolution principles

The format is designed according to the following principles:

* a human can read the file without special tools;
* changes should be minimal and show up well in Git;
* new features must be added without breaking compatibility;
* Markdown remains the primary way to document examples;
* YAML contains only global document settings;
* executable code is stored in standard fenced code blocks.
