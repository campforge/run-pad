using System.Net;
using System.Text;
using Invoklet.Connectors.Http;
using Invoklet.Contracts.Connections;
using Invoklet.Format.MdRun;
using Xunit;

namespace Invoklet.NonUI.Tests.Connectors.Http;

// An HTTP connection with an OpenAPI document is described by it; without one it has nothing to describe and asks nobody.
public class HttpConnectionSchemaTests
{
    private sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "") : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static Dictionary<string, MdRunValue> Params(string? openApi, string baseAddress = "https://api.local/v1/") =>
        new(StringComparer.Ordinal)
        {
            ["baseAddress"] = MdRunValue.Of(baseAddress),
            ["openApi"] = MdRunValue.Of(openApi ?? ""),
            ["headers"] = MdRunValue.Map([new("Authorization", MdRunValue.Of("Bearer t0k"))]),
        };

    [Fact]
    public async Task FetchContractAsync_NoDocumentSet_DescribesNothingAndSendsNoRequest()
    {
        var handler = new RecordingHandler();
        var sut = new HttpConnectionDriver(() => handler);

        var schema = await sut.FetchContractAsync(Params(null), TestContext.Current.CancellationToken);

        Assert.Equal("No OpenAPI document is set for this connection.", schema.Summary);
        Assert.Equal("[]", schema.Data);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FetchContractAsync_ARelativeAddress_IsReadFromTheBaseAddressWithTheHeaders()
    {
        var handler = new RecordingHandler(body: "{ \"paths\": { \"/pets\": { \"get\": { \"responses\": { \"200\": { } } } } } }");
        var sut = new HttpConnectionDriver(() => handler);

        var schema = await sut.FetchContractAsync(Params("openapi.json"), TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.local/v1/openapi.json", request.RequestUri!.ToString());
        Assert.Equal("Bearer t0k", request.Headers.Authorization!.ToString());
        Assert.Equal("1 operation.", schema.Summary);
        Assert.Contains("\"Path\":\"/pets\"", schema.Data);
    }

    [Fact]
    public async Task FetchContractAsync_TheServerRefuses_SaysWhichAddressAndWhatItAnswered()
    {
        var sut = new HttpConnectionDriver(() => new RecordingHandler(HttpStatusCode.NotFound));

        var thrown = await Assert.ThrowsAsync<OpenApiException>(() => sut.FetchContractAsync(Params("https://docs.local/spec.json"), TestContext.Current.CancellationToken));

        Assert.Equal("The server answered 404 Not Found for https://docs.local/spec.json.", thrown.Message);
    }
}
