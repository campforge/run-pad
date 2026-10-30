using System.Net;
using Invoklet.Connectors.Http;
using Invoklet.Contracts.Connections;
using Invoklet.Format.MdRun;
using Xunit;

namespace Invoklet.NonUI.Tests;

public class ConnectionPlaceholderResolverTests
{
    [Fact]
    public void Placeholders_Should_BeReplacedEverywhere_AndMissingOnesReported()
    {
        var parameters = new Dictionary<string, MdRunValue>
        {
            ["url"] = MdRunValue.Of("https://${env:API_HOST}/v1"),
            ["headers"] = MdRunValue.Map([new("Authorization", MdRunValue.Of("Bearer ${secret:token}")), new("X-Literal", MdRunValue.Of("$${keep}"))]),
            ["list"] = MdRunValue.List([MdRunValue.Of("${secret:token}"), MdRunValue.Of("plain")]),
        };
        var secrets = new Dictionary<string, string> { ["token"] = "t0k" };
        var env = new Dictionary<string, string> { ["API_HOST"] = "api.local" };

        var result = ConnectionPlaceholderResolver.Resolve(parameters, secrets.GetValueOrDefault, env.GetValueOrDefault);
        Assert.Empty(result.Errors);
        Assert.Equal("https://api.local/v1", result.Params["url"].Text);
        Assert.Equal("Bearer t0k", result.Params["headers"].Fields!["Authorization"].Text);
        Assert.Equal("${keep}", result.Params["headers"].Fields!["X-Literal"].Text);
        Assert.Equal(["t0k", "plain"], result.Params["list"].Items!.Select(i => i.Text));
        Assert.Equal("Bearer ${secret:token}", parameters["headers"].Fields!["Authorization"].Text); // the input is untouched

        var missing = ConnectionPlaceholderResolver.Resolve(parameters, _ => null, _ => null);
        Assert.Equal(
            [
                "The environment variable 'API_HOST' is not set.",
                "The Secret Store has no secret named 'token'. Add it in Tools → Secret Store.",
            ],
            missing.Errors);
    }

    [Fact]
    public void TheRegistry_Should_FindDriversLetterCaseAside_AndReplaceInPlace()
    {
        var registry = new ConnectionDriverRegistry();
        var first = new HttpConnectionDriver();
        var second = new HttpConnectionDriver();
        registry.Register(first);
        Assert.Same(first, registry.Find("HTTP"));
        Assert.Null(registry.Find("postgres"));
        registry.Register(second);
        Assert.Same(second, Assert.Single(registry.Drivers));
    }
}

public class HttpConnectionDriverTests
{
    private static MdRunConnection Http(params (string Key, MdRunValue Value)[] parameters) => new()
    {
        Name = "api",
        Driver = "http",
        Params = parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
    };

    [Fact]
    public void Validate_Should_CheckTheAddressHeadersAndTimeout()
    {
        var driver = new HttpConnectionDriver();
        Assert.Empty(driver.Validate(Http(("baseAddress", MdRunValue.Of("https://api.example.com/")))));
        Assert.Empty(driver.Validate(Http(("baseAddress", MdRunValue.Of("https://${env:HOST}/")), ("timeout", MdRunValue.Of("${env:T}")))));
        Assert.Empty(driver.Validate(Http(("baseAddress", MdRunValue.Of("http://localhost:5000")), ("headers", MdRunValue.Of("")))));

        Assert.Equal(["The base address is missing."], driver.Validate(Http()));
        Assert.Equal(["The base address 'ftp://files' is not an http:// or https:// address."],
            driver.Validate(Http(("baseAddress", MdRunValue.Of("ftp://files")))));
        Assert.Equal(["Headers must be names with texts.", "The timeout '-1' is not a number of seconds from 0.001 to 2147483."],
            driver.Validate(Http(("baseAddress", MdRunValue.Of("https://a/")),
                ("headers", MdRunValue.List([MdRunValue.Of("x")])), ("timeout", MdRunValue.Of("-1")))));
    }

    // What HttpClient.Timeout takes: a whole number of milliseconds, at most int.MaxValue.
    [Theory]
    [InlineData("0.001", true)]
    [InlineData("2147483", true)]
    [InlineData("1e2", true)]
    [InlineData("0", false)]
    [InlineData("0.0001", false)]
    [InlineData("2147484", false)]
    [InlineData("1e300", false)]
    [InlineData("Infinity", false)]
    [InlineData("NaN", false)]
    [InlineData("soon", false)]
    public void Validate_Should_AcceptOnlyTimeoutsHttpClientTakes(string seconds, bool valid)
    {
        var problems = new HttpConnectionDriver().Validate(Http(("baseAddress", MdRunValue.Of("https://a/")), ("timeout", MdRunValue.Of(seconds))));
        if (valid) Assert.Empty(problems);
        else Assert.Equal([$"The timeout '{seconds}' is not a number of seconds from 0.001 to 2147483."], problems);
    }

    // The generated client would drop them without a word.
    [Fact]
    public void Validate_Should_RejectHeaders_TheClientCantSend()
    {
        var problems = new HttpConnectionDriver().Validate(Http(("baseAddress", MdRunValue.Of("https://a/")),
            ("headers", MdRunValue.Map(
            [
                new("Authorization", MdRunValue.Of("Bearer ${secret:token}")),
                new("X-Api-Key", MdRunValue.Of("k")),
                new("Content-Type", MdRunValue.Of("application/json")),
                new("My Header", MdRunValue.Of("x")),
            ]))));
        Assert.Equal(
        [
            "'Content-Type' can't be sent as a request header: content headers such as Content-Type go with a request's content, and a name has no spaces.",
            "'My Header' can't be sent as a request header: content headers such as Content-Type go with a request's content, and a name has no spaces.",
        ], problems);
    }

    [Fact]
    public async Task Test_Should_RejectATimeoutHttpClientDoesntTake()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var result = await new HttpConnectionDriver(() => handler)
            .TestAsync(Resolved("https://a/", timeout: "Infinity"), TestContext.Current.CancellationToken);
        Assert.Equal(new ConnectionTestResult(false, "The timeout 'Infinity' is not a number of seconds from 0.001 to 2147483."), result);
        Assert.Null(handler.Request);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return send(request, cancellationToken);
        }
    }

    private static Dictionary<string, MdRunValue> Resolved(string address, string? authorization = null, string? timeout = null)
    {
        var parameters = new Dictionary<string, MdRunValue> { ["baseAddress"] = MdRunValue.Of(address) };
        if (authorization is not null) parameters["headers"] = MdRunValue.Map([new("Authorization", MdRunValue.Of(authorization))]);
        if (timeout is not null) parameters["timeout"] = MdRunValue.Of(timeout);
        return parameters;
    }

    [Fact]
    public async Task Test_Should_SendTheHeaders_AndReportTheAnswer()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var result = await new HttpConnectionDriver(() => handler)
            .TestAsync(Resolved("https://api.example.com/v1/", "Bearer t0k"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("The server answered 404 Not Found.", result.Message);
        Assert.Equal(new Uri("https://api.example.com/v1/"), handler.Request!.RequestUri);
        Assert.Equal("Bearer t0k", handler.Request.Headers.Authorization!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RejectedCredentials_Should_FailTheTest(HttpStatusCode status)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        var result = await new HttpConnectionDriver(() => handler).TestAsync(Resolved("https://a/"), TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Contains("check the credentials", result.Message);
    }

    [Fact]
    public async Task AnUnreachableOrSlowServer_Should_FailTheTest_WithTheReason()
    {
        var refused = new FakeHandler((_, _) => throw new HttpRequestException("Connection refused"));
        var result = await new HttpConnectionDriver(() => refused).TestAsync(Resolved("https://a/"), TestContext.Current.CancellationToken);
        Assert.Equal(new ConnectionTestResult(false, "Could not reach the server: Connection refused"), result);

        var slow = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var timedOut = await new HttpConnectionDriver(() => slow).TestAsync(Resolved("https://a/", timeout: "0.2"), TestContext.Current.CancellationToken);
        Assert.Equal(new ConnectionTestResult(false, "The server did not answer within 0.2 seconds."), timedOut);

        var notHttp = await new HttpConnectionDriver(() => slow).TestAsync(Resolved("mailto:x@y"), TestContext.Current.CancellationToken);
        Assert.False(notHttp.Succeeded);
    }
}
