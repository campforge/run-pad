using System.Text.Json;
using Invoklet.Contracts.Connections;
using Invoklet.Format.MdRun;

namespace Invoklet.Connectors.Http;

/// <summary>
/// An HTTP API described by an OpenAPI or Swagger document: the same <c>HttpClient</c> as an HTTP connection, with the base address, headers and
/// timeout, and a typed method for each operation and a class for each schema, as the gRPC connection gives a client for each service:
/// <c>await Connections.Pets.GetPetAsync(7)</c>. The operations are also browsed in Tools → Connections.
/// </summary>
public sealed class OpenApiConnectionDriver : IConnectionSchemaDriver
{
    public const string DriverId = "openapi";

    private const string BaseAddress = "baseAddress";
    private const string Headers = "headers";

    private readonly Func<HttpMessageHandler>? _handler;
    private readonly HttpConnectionDriver _http;

    /// <param name="handler">What sends the requests; a plain <see cref="HttpClientHandler"/> unless replaced.</param>
    public OpenApiConnectionDriver(Func<HttpMessageHandler>? handler = null)
    {
        _handler = handler;
        _http = new HttpConnectionDriver(handler);
    }

    public string Id => DriverId;

    public string DisplayName => "OpenAPI/Swagger";

    public string SchemaVersion => "1";

    public ConnectionCategory Category => ConnectionCategory.Http;

    public IReadOnlyDictionary<string, string> Packages => _http.Packages;

    // The parameters of an HTTP connection and nothing more: the document is found at the usual addresses under the base address.
    public IReadOnlyList<ConnectionParam> Params => _http.Params;

    public IReadOnlyList<string> Validate(MdRunConnection connection) => _http.Validate(connection);

    public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, CancellationToken cancellationToken) =>
        _http.TestAsync(resolvedParams, cancellationToken);

    public ConnectionClientCode CreateClient(MdRunConnection connection, ConnectionCodeContext context)
    {
        var document = context.Schema is { } schema ? JsonSerializer.Deserialize<OpenApiDocument>(schema.Data) : null;
        return HttpConnectionDriver.BuildClient(connection, context, document is null ? "" : OpenApiClientCode.Members(document, context.ClassName), "OpenAPI connection");
    }

    // Where APIs usually publish their description, tried in this order under the base address and then under the address's root.
    internal static readonly string[] DocumentPaths =
        ["openapi.json", "swagger.json", "swagger/v1/swagger.json", "v3/api-docs", "v2/api-docs", "api-docs", "docs/openapi.json"];

    public async Task<ConnectionSchema> FetchSchemaAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, ConnectionTools tools,
        CancellationToken cancellationToken)
    {
        if (!resolvedParams.TryGetValue(BaseAddress, out var addressValue) || addressValue.Text is not { } address || !HttpConnectionDriver.IsHttpAddress(address))
            throw new OpenApiException("The base address is not an http:// or https:// address.");
        var baseUri = new Uri(address);
        var root = new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/");
        var candidates = DocumentPaths.Select(path => new Uri(baseUri, path))
            .Concat(DocumentPaths.Select(path => new Uri(root, path)))
            .Distinct().ToList();

        using var client = new HttpClient(_handler?.Invoke() ?? new HttpClientHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        string? lastProblem = null;
        foreach (var uri in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            foreach (var (name, value) in resolvedParams.TryGetValue(Headers, out var headerFields) ? headerFields.Fields ?? [] : [])
                if (value.Text is { } headerValue) request.Headers.TryAddWithoutValidation(name, headerValue);
            try
            {
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                    throw new OpenApiException($"The server answered {(int)response.StatusCode} for {uri}: check the credentials in the headers.");
                if (!response.IsSuccessStatusCode) continue;
                var document = OpenApiReader.Read(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var count = document.Operations.Count;
                return new ConnectionSchema(count == 1 ? "1 operation." : $"{count} operations.", [], JsonSerializer.Serialize(document));
            }
            catch (OpenApiException ex) when (ex.Message.StartsWith("The document", StringComparison.Ordinal) || ex.Message.StartsWith("The OpenAPI document", StringComparison.Ordinal))
            {
                // Something answered there, but it is not a description (a page, YAML): try the next address.
                lastProblem = ex.Message;
            }
            catch (HttpRequestException ex)
            {
                lastProblem = ex.Message;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastProblem = $"{uri} did not answer in time.";
            }
        }
        throw new OpenApiException($"No OpenAPI or Swagger document (JSON) found under {baseUri}: tried {string.Join(", ", DocumentPaths.Take(3))} and other usual addresses."
            + (lastProblem is null ? "" : " " + lastProblem));
    }
}
