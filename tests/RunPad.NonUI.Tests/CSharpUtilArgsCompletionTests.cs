using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using Xunit;

namespace RunPad.NonUI.Tests;

public sealed class CSharpUtilArgsCompletionTests
{
    [Fact]
    public async Task Completion_Should_ExposeTheEnumTypeAndItsValues()
    {
        var references = new CSharpReferenceService();
        using var service = new CSharpIntelliSenseService(
            references.GetDefault().Append(references.GetExtensionReference()),
            NullLogger<CSharpIntelliSenseService>.Instance);
        service.UpdateArgumentContract(new RpadArgumentContract([new("env", "enum", ["prod"], ["dev", "prod"])]));
        var properties = await service.GetCompletionsAsync("Arguments.", "Arguments.".Length,
            ct: TestContext.Current.CancellationToken);
        Assert.Contains("ArgumentsEnv", properties!.Items.Single(i => i.DisplayText == "Env").Summary ?? "");
        var values = await service.GetCompletionsAsync("ArgumentsEnv.", "ArgumentsEnv.".Length,
            ct: TestContext.Current.CancellationToken);
        Assert.NotNull(values);
        Assert.Contains(values.Items, i => i.DisplayText == "Dev");
        Assert.Contains(values.Items, i => i.DisplayText == "Prod");
    }

    [Fact]
    public async Task Completion_Should_OfferTypedArgumentMembers()
    {
        var references = new CSharpReferenceService();
        using var service = new CSharpIntelliSenseService(
            references.GetDefault().Append(references.GetExtensionReference()),
            NullLogger<CSharpIntelliSenseService>.Instance);
        service.UpdateArgumentContract(new RpadArgumentContract(
        [
            new("Environment", "string", ["test"], ["dev", "test"]),
            new("Count", "int", ["3"], []),
        ]));

        var result = await service.GetCompletionsAsync("Arguments.", "Arguments.".Length,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(["Count", "Environment"], result!.Items.Select(item => item.DisplayText).Order());
        Assert.Contains("int", result.Items.Single(item => item.DisplayText == "Count").Summary ?? "");
    }
}
