using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.RPad;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Where an argument starts, completion offers `name:` for the parameters still open.
public class CSharpIntelliSenseNamedArgumentTests
{
    private const string Declarations =
        "\nstatic class Net\n{\n" +
        "    public static void Send(string host, int port = 80, bool secure = false) { }\n" +
        "    public static void Send(string host, TimeSpan timeout) { }\n}\n" +
        "class Client { public Client(string url, int retries = 3) { } }\n";

    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        var service = new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance);
        service.UpdateKind(RpadKind.Statements);
        return service;
    }

    private static async Task<IReadOnlyList<string>> NamedArgumentsAsync(string code)
    {
        using var service = CreateService();
        code += Declarations;
        var position = code.IndexOf('|');
        code = code.Remove(position, 1);
        var list = await service.GetCompletionsAsync(code, position, CompletionTrigger.Invoked, TestContext.Current.CancellationToken);
        return list?.Items.OfType<SimpleCompletionItem>()
            .Where(i => i.Source == CompletionSourceKind.NamedArgument)
            .Select(i => i.DisplayText)
            .Order(StringComparer.Ordinal)
            .ToList() ?? [];
    }

    [Fact]
    public async Task FirstArgument_Should_OfferEveryOverloadsParameters()
    {
        Assert.Equal(["host:", "port:", "secure:", "timeout:"], await NamedArgumentsAsync("Net.Send(h|"));
    }

    [Fact]
    public async Task LaterArgument_Should_SkipParametersGivenByPosition()
    {
        Assert.Equal(["port:", "secure:", "timeout:"], await NamedArgumentsAsync("Net.Send(\"a\", p|"));
    }

    [Fact]
    public async Task LaterArgument_Should_KeepOnlyOverloadsTheTypedArgumentsFit()
    {
        Assert.Equal(["secure:"], await NamedArgumentsAsync("Net.Send(\"a\", 1, s|"));
    }

    [Fact]
    public async Task ParametersAlreadyNamed_Should_NotBeOfferedAgain()
    {
        Assert.Equal(["port:", "timeout:"], await NamedArgumentsAsync("Net.Send(\"a\", secure: true, p|"));
    }

    [Fact]
    public async Task Constructor_Should_OfferItsParameters()
    {
        Assert.Equal(["retries:", "url:"], await NamedArgumentsAsync("var c = new Client(u|"));
    }

    [Theory]
    [InlineData("Net.Send(host: |")]
    [InlineData("Net.Send(\"a\".|")]
    [InlineData("var x = |")]
    public async Task OutsideAnArgumentsStart_Should_OfferNoNames(string code)
    {
        Assert.Empty(await NamedArgumentsAsync(code));
    }
}
