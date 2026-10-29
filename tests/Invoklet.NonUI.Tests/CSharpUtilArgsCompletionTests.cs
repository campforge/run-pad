using Invoklet.Contracts.Connections;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Contracts.Models;
using Invoklet.Format.RPad;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class CSharpUtilArgsCompletionTests
{
    private static CSharpIntelliSenseService CreateService(CSharpReferenceService references) =>
        new(references.GetDefault().Append(references.GetExtensionReference()),
            NullLogger<CSharpIntelliSenseService>.Instance);

    [Fact]
    public async Task Completion_Should_ExposeTheEnumTypeAndItsValues()
    {
        var references = new CSharpReferenceService();
        using var service = CreateService(references);
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
        using var service = CreateService(references);
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

    [Fact]
    public async Task GeneratedRoots_OfferMembersWithHigherPriorityAndPrefixFiltering()
    {
        var references = new CSharpReferenceService();
        using var service = CreateService(references);
        service.UpdateExecutionContext(new ExecutionRequest
        {
            Connections = new GeneratedConnections(
                "public static partial class Connections { public static BillingClient Billing => null!; public static AuditClient Audit => null!; }",
                [], [])
        });

        var connections = await service.GetCompletionsAsync("Connections.Bil", "Connections.Bil".Length,
            ct: TestContext.Current.CancellationToken);
        Assert.NotNull(connections);
        var billing = Assert.Single(connections!.Items, i => i.DisplayText == "Billing");
        Assert.DoesNotContain(connections.Items, i => i.DisplayText == "Audit");
        Assert.True(billing.Priority > 1.0);
        Assert.StartsWith("0", billing.SortText, StringComparison.Ordinal);

        var util = await service.GetCompletionsAsync("Util.Canc", "Util.Canc".Length,
            ct: TestContext.Current.CancellationToken);
        Assert.NotNull(util);
        var cancellation = Assert.Single(util!.Items, i => i.DisplayText == "CancellationToken");
        Assert.True(cancellation.Priority > 1.0);
        Assert.StartsWith("0", cancellation.SortText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtensionMethods_GetTheirOwnKindAndSlightlyBeatOrdinaryMethods()
    {
        var references = new CSharpReferenceService();
        using var service = CreateService(references);
        var code = "var value = 1; value.Dum";
        var result = await service.GetCompletionsAsync(code, code.Length,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var dump = Assert.Single(result!.Items, i => i.DisplayText == "Dump");
        Assert.Equal(CompletionItemKind.ExtensionMethod, dump.Kind);
        Assert.True(dump.Priority > 0.8);
        Assert.All(result.Items.Where(i => i.DisplayText.StartsWith("Du", StringComparison.Ordinal)),
            item => Assert.StartsWith("1", item.SortText, StringComparison.Ordinal));
    }
}
