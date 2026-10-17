using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Contracts.IntelliSense;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// In the Program kind the editor checks the script inside a class the user never sees, so Roslyn's
// formatter took the script's members for members of that class and indented what a lightbulb action
// writes one level deeper than the visible code: braces added under an `if` at the wrong depth, the
// method's own closing brace moved, a member line shifted right. The result must be what the same
// action gives for the same code without the hidden class.
public sealed class CodeActionWrapperIndentationTests
{
    private static CSharpIntelliSenseService CreateService(RpadKind kind)
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
    private static async Task<string> ApplyAsync(CSharpIntelliSenseService service, string code, string title, int from = 0)
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
                var applied = await service.MaterializeFixAsync(code, codeFix.Key, TestContext.Current.CancellationToken);
                Assert.NotNull(applied);
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
        using var service = CreateService(RpadKind.Program);

        var result = await ApplyAsync(service, code, "Add braces");

        Assert.Equal("void Main()\n{\n    if (DateTime.Now.Year > 2000)\n    {\n        Console.WriteLine(1);\n    }\n}\n", result);
    }

    [Fact]
    public async Task AddAccessibilityModifiers_InTheProgramKind_Should_KeepTheMemberAtItsColumn()
    {
        const string code = "void Main()\n{\n    Console.WriteLine(1);\n}\n";
        using var service = CreateService(RpadKind.Program);

        var result = await ApplyAsync(service, code, "Add accessibility modifiers");

        Assert.Equal("private void Main()\n{\n    Console.WriteLine(1);\n}\n", result);
    }

    // Re-formatting the changed lines keeps a line aligned to a token's column where the action put it,
    // and the lines the action did not touch keep the user's own (unusual) indentation.
    [Fact]
    public async Task AnAlignedWrap_InTheProgramKind_Should_KeepItsAlignment_AndOtherLines()
    {
        const string code = "void Main()\n{\n    Console.WriteLine(Twice(2));\n}\n\nint Twice(int v)\n{\n    return v * 2;\n}\n\nint Odd()\n{\n  return 1;\n}\n";
        using var service = CreateService(RpadKind.Program);

        var aligned = await ApplyAsync(service, code, "Wrap and align expression", code.IndexOf("return v", StringComparison.Ordinal));

        Assert.Equal(code.Replace("    return v * 2;", "    return v\n           * 2;"), aligned);
    }

    // Nothing changes for the Statements kind, which has no hidden class.
    [Fact]
    public async Task AddBraces_InTheStatementsKind_Should_StayAsBefore()
    {
        const string code = "if (DateTime.Now.Year > 2000) Console.WriteLine(1);\nConsole.WriteLine(2);\n";
        using var service = CreateService(RpadKind.Statements);

        var result = await ApplyAsync(service, code, "Add braces");

        Assert.Equal("if (DateTime.Now.Year > 2000)\n{\n    Console.WriteLine(1);\n}\n\nConsole.WriteLine(2);\n", result);
    }
}
