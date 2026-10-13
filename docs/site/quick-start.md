---
title: Quick start
description: Write, run and save a first C# script in RunPad.
---

# Quick start

This walkthrough takes about five minutes. It assumes RunPad starts without the setup
window, or that you have finished it (see [Installation and first run](install.md)).

## 1. Create a script

Choose **File → New File** or press ++ctrl+n++. RunPad opens an empty, untitled tab.
New files use the language and kind set in **Preferences → Editor → New file defaults**,
which are C# and *Statements* unless you changed them.

To pick the language and kind yourself, use **File → New RPad File → C#** or
**Visual Basic** instead and choose a kind in the dialog.

!!! tip "Shortcuts on macOS"
    RunPad's own shortcuts currently use the Control key on every platform, including
    macOS. ++ctrl+n++ means Control-N, not Command-N. See
    [Keyboard shortcuts](reference.md#keyboard-shortcuts).

## 2. Type the code

Paste this into the editor:

```csharp
var languages = new[] { "C#", "Visual Basic" };
Console.WriteLine($"Hello from RunPad! {languages.Length} languages are supported.");

languages
    .Select((name, index) => new { Position = index + 1, Name = name, Letters = name.Length })
    .Dump("Supported languages");
```

While you type, completion lists appear after `.`. Press ++ctrl+space++ to open them
yourself. The editor shows only the code. The file's header and surrounding Markdown are
managed for you; see [Scripts and .rpad files](scripts.md).

## 3. Run it

Press ++f5++ or click the **Run** button (the play icon at the left of the toolbar). The status
strip under the editor shows *Process* while the script compiles and runs, then
*Success* and the elapsed time.

The **Results** tab shows:

- the plain text line `Hello from RunPad! 2 languages are supported.`;
- a block headed **Supported languages** with a two-row table:

| Position | Name | Letters |
| --- | --- | --- |
| 1 | "C#" | 2 |
| 2 | "Visual Basic" | 12 |

Strings are shown in quotes so you can tell `"1"` from `1`. To stop a script that runs
too long, press ++escape++ or click the **Stop** button next to Run.

## 4. Save it

Press ++ctrl+s++. Because the tab is untitled, RunPad asks where to save it. Save it in
the **RunPad Queries** folder in your Documents folder so it appears in the file tree on
the left. Once a file has a name, every Run saves it first.

## Try the downloadable version

The same script, with explanatory Markdown around it, is available as
[`quick-start.rpad`](examples/quick-start.rpad). Save it in your
`Documents/RunPad Queries` folder and click it in RunPad's file tree.

## Next steps

- [Scripts and .rpad files](scripts.md): kinds, front matter and the file format.
- [Results and runtime helpers](results.md): more ways to use `.Dump()`.
- [Debugging](debugging.md): set a breakpoint and step through this script.
