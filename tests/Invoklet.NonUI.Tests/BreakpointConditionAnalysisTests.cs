using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

public class BreakpointConditionAnalysisTests
{
    [Theory]
    [InlineData("count == 2", true)]
    [InlineData("count", false)]
    [InlineData("missing == 2", false)]
    [InlineData("count >", false)]
    [InlineData("", true)]
    public async Task Conditions_Should_BeBooleanAndBindAtTheBreakpoint(string expression, bool valid)
    {
        using var service = new CSharpIntelliSenseService(new CSharpReferenceService().GetDefault(), NullLogger<CSharpIntelliSenseService>.Instance);
        const string code = "int count = 2;\nstring name = \"test\";\nSystem.Console.WriteLine(count);";
        var result = await service.AnalyzeBreakpointConditionAsync(code, code.IndexOf("System.Console", StringComparison.Ordinal), expression, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(valid, result.Error is null);
    }

    [Fact]
    public async Task ProgramCondition_Should_UseTheVisibleMethodsLocals()
    {
        using var service = new CSharpIntelliSenseService(new CSharpReferenceService().GetDefault(), NullLogger<CSharpIntelliSenseService>.Instance);
        service.UpdateKind(Invoklet.Format.MdRun.MdRunKind.Program);
        const string code = "void Main() { int count = 2; System.Console.WriteLine(count); }";
        var result = await service.AnalyzeBreakpointConditionAsync(code, code.IndexOf("System.Console", StringComparison.Ordinal),
            "count > 1", TestContext.Current.CancellationToken);
        Assert.Null(result!.Error);
    }

    [Fact]
    public async Task Suggestions_Should_IncludeScopedLocalsAndMembers()
    {
        using var service = new CSharpIntelliSenseService(new CSharpReferenceService().GetDefault(), NullLogger<CSharpIntelliSenseService>.Instance);
        const string code = "string name = \"test\";\nSystem.Console.WriteLine(name);";
        var position = code.IndexOf("System.Console", StringComparison.Ordinal);
        var locals = await service.AnalyzeBreakpointConditionAsync(code, position, "na", TestContext.Current.CancellationToken);
        Assert.Contains("name", locals!.Suggestions);
        var members = await service.AnalyzeBreakpointConditionAsync(code, position, "name.Le", TestContext.Current.CancellationToken);
        Assert.Contains("Length", members!.Suggestions);
    }
}
