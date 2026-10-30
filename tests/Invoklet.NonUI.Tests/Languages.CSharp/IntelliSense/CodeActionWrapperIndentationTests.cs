using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense;

// In the Program kind the editor checks the script inside a class the user never sees, so Roslyn's
// formatter took the script's members for members of that class and indented what a lightbulb action
// writes one level deeper than the visible code: braces added under an `if` at the wrong depth, the
// method's own closing brace moved, a member line shifted right. The result must be what the same
// action gives for the same code without the hidden class.
[Collection(CodeActionAllocationCollection.Name)]
public sealed class CodeActionWrapperIndentationTests
{
    private static CSharpIntelliSenseService CreateService(MdRunKind kind)
    {
        var referenceService = new CSharpReferenceService();
        var service = new CSharpIntelliSenseService(
            referenceService.GetDefault().Append(referenceService.GetExtensionReference()), NullLogger<CSharpIntelliSenseService>.Instance)
        {
            BackgroundAnalysisDebounce = TimeSpan.Zero,
        };
        service.UpdateKind(kind);
        return service;
    }

    // The lightbulb action with this title — a code fix of the background analysis, or else the
    // refactoring offered at the first position that has it — applied.
    private static async Task<string> ApplyAsync(CSharpIntelliSenseService service, string code, string title, int from = 0,
        long? allocationBudget = null)
    {
        var listed = new TaskCompletionSource<IReadOnlyList<ICodeFixInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFixes(string analyzed, IReadOnlyList<ICodeFixInfo> fixes)
        {
            if (analyzed == code) listed.TrySetResult(fixes);
        }
        service.CodeFixesChanged += OnFixes;
        try
        {
            service.NotifyTextChanged(code);
            var codeFix = (await listed.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken))
                .FirstOrDefault(f => f.Title == title);
            if (codeFix is not null)
            {
                var before = GC.GetTotalAllocatedBytes(precise: true);
                var applied = await service.MaterializeFixAsync(code, codeFix.Key, TestContext.Current.CancellationToken);
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                Assert.NotNull(applied);
                if (allocationBudget is { } budget)
                    Assert.True(allocated < budget, $"applying the fix allocated {allocated:N0} bytes");
                return applied.NewCode;
            }
        }
        finally
        {
            service.CodeFixesChanged -= OnFixes;
        }

        for (var position = from; position < code.Length; position++)
        {
            var refactorings = await service.GetRefactoringsAsync(code, position, TestContext.Current.CancellationToken);
            var match = refactorings?.FirstOrDefault(r => r.Title == title);
            if (match is null) continue;
            var fix = await service.MaterializeFixAsync(code, match.Key, TestContext.Current.CancellationToken);
            Assert.NotNull(fix);
            return fix.NewCode;
        }
        Assert.Fail($"'{title}' was not offered");
        return "";
    }

    [Fact]
    public async Task AddBraces_InTheProgramKind_Should_IndentRelativeToTheVisibleCode()
    {
        const string code = "void Main()\n{\n    if (DateTime.Now.Year > 2000) Console.WriteLine(1);\n}\n";
        using var service = CreateService(MdRunKind.Program);

        var result = await ApplyAsync(service, code, "Add braces");

        Assert.Equal("void Main()\n{\n    if (DateTime.Now.Year > 2000)\n    {\n        Console.WriteLine(1);\n    }\n}\n", result);
    }

    // With an extension class the script is split around the classes compiled outside the hidden
    // class: code inside it is still analysed one level deeper, code in a moved class is not.
    [Fact]
    public async Task AddBraces_InAProgramWithAnExtensionClass_Should_IndentRelativeToTheVisibleCode()
    {
        const string extensions = "static class Extensions\n{\n    public static int Twice(this int n) => n * 2;\n}\n";
        const string code = "void Main()\n{\n    if (DateTime.Now.Year > 2000) Console.WriteLine(1.Twice());\n}\n" + extensions;
        using var service = CreateService(MdRunKind.Program);

        var result = await ApplyAsync(service, code, "Add braces");

        Assert.Equal("void Main()\n{\n    if (DateTime.Now.Year > 2000)\n    {\n        Console.WriteLine(1.Twice());\n    }\n}\n" + extensions, result);
    }

    [Fact]
    public async Task AddBraces_InsideAMovedExtensionClass_Should_KeepItsOwnIndentation()
    {
        const string main = "void Main()\n{\n    Console.WriteLine(1.Twice());\n}\n";
        const string code = main + "static class Extensions\n{\n    public static int Twice(this int n)\n    {\n        if (n > 0) return n * 2;\n        return 0;\n    }\n}\n";
        using var service = CreateService(MdRunKind.Program);

        var result = await ApplyAsync(service, code, "Add braces", from: main.Length);

        Assert.Equal(main + "static class Extensions\n{\n    public static int Twice(this int n)\n    {\n        if (n > 0)\n        {\n            return n * 2;\n        }\n\n        return 0;\n    }\n}\n", result);
    }

    [Fact]
    public async Task AddAccessibilityModifiers_InTheProgramKind_Should_KeepTheMemberAtItsColumn()
    {
        const string code = "void Main()\n{\n    Console.WriteLine(1);\n}\n";
        using var service = CreateService(MdRunKind.Program);

        var result = await ApplyAsync(service, code, "Add accessibility modifiers");

        Assert.Equal("private void Main()\n{\n    Console.WriteLine(1);\n}\n", result);
    }

    // Re-formatting the changed lines keeps a line aligned to a token's column where the action put it,
    // and the lines the action did not touch keep the user's own (unusual) indentation.
    [Fact]
    public async Task AnAlignedWrap_InTheProgramKind_Should_KeepItsAlignment_AndOtherLines()
    {
        const string code = "void Main()\n{\n    Console.WriteLine(Twice(2));\n}\n\nint Twice(int v)\n{\n    return v * 2;\n}\n\nint Odd()\n{\n  return 1;\n}\n";
        using var service = CreateService(MdRunKind.Program);

        var aligned = await ApplyAsync(service, code, "Wrap and align expression", code.IndexOf("return v", StringComparison.Ordinal));

        Assert.Equal(code.Replace("    return v * 2;", "    return v\n           * 2;"), aligned);
    }

    // The continuation line Wrap expression breaks off is indented like the Statements kind's — at
    // the method's level and one block deeper — while the aligned variant keeps its column.
    [Theory]
    [InlineData("    ", "        ")]
    [InlineData("        ", "            ")]
    public async Task WrapExpression_InTheProgramKind_Should_MatchTheStatementsKind(string statementIndent, string continuationIndent)
    {
        var body = statementIndent == "    "
            ? "    return v * 2;\n"
            : "    if (v > 0)\n    {\n        return v * 2;\n    }\n    return 0;\n";
        var program = "void Main()\n{\n    Console.WriteLine(Twice(2));\n}\n\nint Twice(int v)\n{\n" + body + "}\n";
        var statements = "Console.WriteLine(Twice(2));\n\nint Twice(int v)\n{\n" + body + "}\n";
        var from = program.IndexOf("return v", StringComparison.Ordinal);

        using var programService = CreateService(MdRunKind.Program);
        using var statementsService = CreateService(MdRunKind.Statements);
        var expected = (await ApplyAsync(statementsService, statements, "Wrap expression", statements.IndexOf("return v", StringComparison.Ordinal)))
            .Replace("Console.WriteLine(Twice(2));\n\n", "");
        Assert.Contains("\n" + statementIndent + "return v\n" + continuationIndent + "* 2;\n", expected);

        var wrapped = await ApplyAsync(programService, program, "Wrap expression", from);

        Assert.Equal("void Main()\n{\n    Console.WriteLine(Twice(2));\n}\n\n" + expected, wrapped);
    }

    // Lines the action did not touch keep tabs as the user wrote them.
    [Fact]
    public async Task AddBraces_InTheProgramKind_Should_KeepUntouchedTabIndentedLines()
    {
        const string code = "void Main()\n{\n    if (DateTime.Now.Year > 2000) Console.WriteLine(1);\n}\n\nint Other()\n{\n\treturn 1;\n}\n";
        using var service = CreateService(MdRunKind.Program);

        var result = await ApplyAsync(service, code, "Add braces");

        Assert.Equal("void Main()\n{\n    if (DateTime.Now.Year > 2000)\n    {\n        Console.WriteLine(1);\n    }\n}\n\nint Other()\n{\n\treturn 1;\n}\n", result);
    }

    // A one-line fix in a large script: only that place changes, and correcting it costs memory in
    // proportion to the script, not to its square (a line-by-line comparison table for 10,000 lines
    // took about 400 MB).
    [Fact]
    public async Task AFixInALargeProgramScript_Should_ChangeOnlyItsLines_WithBoundedMemory()
    {
        var members = string.Concat(Enumerable.Range(0, 2_500).Select(i => $"int Value{i}()\n{{\n    return {i};\n}}\n"));
        var code = "void Main()\n{\n    if (DateTime.Now.Year > 2000) Console.WriteLine(1);\n}\n" + members;
        Assert.True(code.Split('\n').Length > 10_000);
        using var service = CreateService(MdRunKind.Program);

        var result = await ApplyAsync(service, code, "Add braces", allocationBudget: 150_000_000);

        Assert.Equal("void Main()\n{\n    if (DateTime.Now.Year > 2000)\n    {\n        Console.WriteLine(1);\n    }\n}\n" + members, result);
    }

    // The correction stops when the request is cancelled, like the rest of the fix.
    [Fact]
    public async Task Reindenting_Should_StopWhenCancelled()
    {
        using var service = CreateService(MdRunKind.Program);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var code = string.Concat(Enumerable.Range(0, 5_000).Select(i => $"int Value{i}() => {i};\n"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ReindentOutsideTheWrapperAsync(code, [new TextSpan(0, 5)], cancelled.Token));
    }

    // Nothing changes for the Statements kind, which has no hidden class.
    [Fact]
    public async Task AddBraces_InTheStatementsKind_Should_StayAsBefore()
    {
        const string code = "if (DateTime.Now.Year > 2000) Console.WriteLine(1);\nConsole.WriteLine(2);\n";
        using var service = CreateService(MdRunKind.Statements);

        var result = await ApplyAsync(service, code, "Add braces");

        Assert.Equal("if (DateTime.Now.Year > 2000)\n{\n    Console.WriteLine(1);\n}\n\nConsole.WriteLine(2);\n", result);
    }
}

// The memory regression measures process-wide allocations across an awaited code action.
// Other test collections must not contribute their allocations to that measurement.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CodeActionAllocationCollection
{
    public const string Name = "CodeActionAllocation";
}
