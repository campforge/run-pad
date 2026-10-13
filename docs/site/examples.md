---
title: Examples
description: Downloadable .rpad example scripts with explanations and expected output.
---

# Examples

These scripts are small and safe: they use only in-memory data, do not read or write
files, do not use the network and do not need NuGet packages. Each one is a complete
`.rpad` file with a short explanation in its Markdown.

**To use one:** download it (right-click the link and save it if your browser opens the
file as text), put it in the `RunPad Queries` folder in your Documents folder, and click
it in RunPad's file tree.

Numbers in the output are formatted with your system's culture, so you may see `49,90`
instead of `49.90`.

## quick-start.rpad

[Download `quick-start.rpad`](examples/quick-start.rpad) · Statements · used in the
[quick start](quick-start.md)

Prints one line and dumps a two-row table.

```text
Hello from RunPad! 2 languages are supported.
Supported languages
  Position | Name           | Letters
  1        | "C#"           | 2
  2        | "Visual Basic" | 12
```

## products-overview.rpad

[Download `products-overview.rpad`](examples/products-overview.rpad) · Statements

LINQ over an in-memory product list, with a `record` declared after the statements.
Produces four Results blocks:

- **All products** — a table of five products with Name, Category, Price and InStock;
- **Out of stock** — a one-item list: `"Laptop stand"`;
- **By category** — three rows (Accessories, Displays, Peripherals) with Count,
  StockValue and a nested **Items** table per category;
- **Total stock value** — `2823.30`.

## program-kind.rpad

[Download `program-kind.rpad`](examples/program-kind.rpad) · Program

A `void Main()` with a static helper method, a class and a record. Output:

```text
Inventory
  Name       | Quantity
  "Keyboard" | 5
  "Mouse"    | 5
Total items: 10
```

## expression-kind.rpad

[Download `expression-kind.rpad`](examples/expression-kind.rpad) · Expression

A single LINQ expression with no `.Dump()` call and no semicolon. RunPad renders the
result as a table with Number, Square and IsLarge for the even numbers 2 to 10; the
heading is the expression's own source text.

## debugging-loop.rpad

[Download `debugging-loop.rpad`](examples/debugging-loop.rpad) · Statements · used in
[Debugging](debugging.md#practice-script)

A five-iteration loop that calls a local function, for practising breakpoints,
conditions, stepping and watches. Without the debugger it prints:

```text
i = 1, square = 1, running total = 1
i = 2, square = 4, running total = 5
i = 3, square = 9, running total = 14
i = 4, square = 16, running total = 30
i = 5, square = 25, running total = 55
Sum of squares 1..5
55
```

## analyzer-diagnostics.rpad

[Download `analyzer-diagnostics.rpad`](examples/analyzer-diagnostics.rpad) · Statements ·
used in [Diagnostics and analyzers](analyzers.md#example-an-always-failing-constant)

**Not meant to run.** Open it to see a compiler error with a `using` quick fix and two
RunPad analyzer warnings (RPAD0258). As downloaded, Run stops with error `CS0103`. After
the quick fix is applied, Run prints `{"Name":"RunPad","Version":1}` and then fails with
`System.OverflowException`, which is what the warnings predict.

## live-output.rpad

[Download `live-output.rpad`](examples/live-output.rpad) · Statements · used in
[Results and runtime helpers](results.md#live-updating-output)

A `Util.ProgressBar` and a `DumpContainer` that update in place for about one second,
then a **Processed values** list: 10, 20, 30, 40, 50. The progress bar ends at 100% with
the caption *Done*, and the status block reads `"Processed 5 of 5 items"`.

## linqpad-sample.linq

[Download `linqpad-sample.linq`](examples/linqpad-sample.linq) · LINQPad query

A LINQPad *Statements* query for trying **File → Import LINQPad Query...** or
`runpad convert`. See [Importing LINQPad queries](cli.md#importing-linqpad-queries).

## Visual Basic

There is no Visual Basic download, but you can try one quickly: choose
**File → New RPad File → Visual Basic**, pick the *Statements* kind, and paste:

```vb
Dim names = {"Ada", "Grace", "Linus"}
For Each name In names
    Console.WriteLine($"Hello, {name}!")
Next
names.Select(Function(n) New With {.Name = n, .Letters = n.Length}).Dump("Names")
```

It prints three greetings and a **Names** table with each name and its length.
