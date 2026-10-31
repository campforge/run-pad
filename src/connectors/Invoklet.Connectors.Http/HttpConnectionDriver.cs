using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Invoklet.Contracts.Connections;
using Invoklet.Format.MdRun;

namespace Invoklet.Connectors.Http;

/// <summary>
/// An HTTP API: a base address the script's requests are relative to, headers sent with every
/// request (an <c>Authorization</c> header with <c>${secret:…}</c>, for instance) and a timeout.
/// </summary>
public sealed class HttpConnectionDriver : IConnectionContractDriver
{
    public const string DriverId = "http";

    private const string BaseAddress = "baseAddress";
    private const string Headers = "headers";
    private const string Timeout = "timeout";
    private const string OpenApi = "openApi";
    private const int DefaultTimeoutSeconds = 100;

    // HttpClient.Timeout takes a whole number of milliseconds up to int.MaxValue, more than 24 days.
    private const double MinTimeoutSeconds = 0.001;
    private const double MaxTimeoutSeconds = 2147483;

    // The Test button waits no longer than this, whatever the connection's own timeout.
    private static readonly TimeSpan MaxTestTime = TimeSpan.FromSeconds(30);

    private readonly Func<HttpMessageHandler> _handler;

    /// <param name="handler">What sends the Test button's request; a plain <see cref="HttpClientHandler"/> unless replaced.</param>
    public HttpConnectionDriver(Func<HttpMessageHandler>? handler = null) => _handler = handler ?? (() => new HttpClientHandler());

    public string Id => DriverId;

    public string DisplayName => "HTTP";

    public ConnectionCategory Category => ConnectionCategory.Http;

    // HttpClient is part of .NET.
    public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string>();

    public IReadOnlyList<ConnectionParam> Params { get; } =
    [
        new(BaseAddress, "Base address", Required: true, Example: "https://api.example.com/",
            Description: "Requests are relative to it. End it with / to keep its path."),
        new(Headers, "Headers", ConnectionParamType.Map,
            Description: "Sent with every request, for example Authorization: Bearer ${secret:api-token}."),
        new(OpenApi, "OpenAPI document", Example: "openapi.json",
            Description: "Optional. The address of the API's OpenAPI (Swagger) description in JSON, absolute or relative to the base address; Tools → Connections then lists its operations."),
        new(Timeout, "Timeout (seconds)", ConnectionParamType.Number, Default: DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            Advanced: true),
    ];

    // The description is the API's own document, fetched with the connection's headers; without one there is nothing to describe, and no request is made.
    public async Task<ConnectionSchema> FetchContractAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, CancellationToken cancellationToken)
    {
        if (!resolvedParams.TryGetValue(OpenApi, out var documentValue) || string.IsNullOrWhiteSpace(documentValue.Text))
            return new ConnectionSchema("No OpenAPI document is set for this connection.", [], "[]");
        if (!resolvedParams.TryGetValue(BaseAddress, out var addressValue) || addressValue.Text is not { } address || !IsHttpAddress(address))
            throw new OpenApiException("The base address is not an http:// or https:// address.");
        if (!Uri.TryCreate(new Uri(address), documentValue.Text.Trim(), out var documentUri) || documentUri.Scheme is not ("http" or "https"))
            throw new OpenApiException($"The OpenAPI document address '{documentValue.Text}' is not an http:// or https:// address.");

        using var client = new HttpClient(_handler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Get, documentUri);
        foreach (var (name, value) in resolvedParams.TryGetValue(Headers, out var h) ? h.Fields ?? [] : [])
            if (value.Text is { } headerValue) request.Headers.TryAddWithoutValidation(name, headerValue);
        string json;
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new OpenApiException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase} for {documentUri}.".Replace("  ", " "));
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OpenApiException($"Could not read {documentUri}: {ex.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OpenApiException($"{documentUri} did not answer in time.");
        }
        var operations = OpenApiReader.Read(json);
        return new ConnectionSchema(operations.Count == 1 ? "1 operation." : $"{operations.Count} operations.", [], JsonSerializer.Serialize(operations));
    }

    public IReadOnlyList<string> Validate(MdRunConnection connection)
    {
        var errors = new List<string>();
        var parameters = connection.Params;
        if (!parameters.TryGetValue(BaseAddress, out var address) || string.IsNullOrWhiteSpace(address.Text))
            errors.Add("The base address is missing.");
        else if (!HasPlaceholder(address.Text) && !IsHttpAddress(address.Text))
            errors.Add($"The base address '{address.Text}' is not an http:// or https:// address.");

        // "headers:" left empty is no headers; anything else must map names to texts.
        if (parameters.TryGetValue(Headers, out var headers) && headers.Text is not { Length: 0 })
        {
            if (headers.Fields is not { } fields || fields.Values.Any(v => v.Text is null))
                errors.Add("Headers must be names with texts.");
            else
                // The client would drop them without a word: content headers such as Content-Type
                // belong to a request's content, and a name can't have spaces or the like.
                errors.AddRange(fields.Keys.Where(k => !IsRequestHeader(k)).Select(k =>
                    $"'{k}' can't be sent as a request header: content headers such as Content-Type go with a request's content, and a name has no spaces."));
        }

        if (parameters.TryGetValue(Timeout, out var timeout) && timeout.Text is { Length: > 0 } seconds
            && !HasPlaceholder(seconds) && !(TimeoutSeconds(seconds) is >= MinTimeoutSeconds and <= MaxTimeoutSeconds))
            errors.Add(TimeoutError(seconds));
        return errors;
    }

    public async Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, CancellationToken cancellationToken)
    {
        if (!resolvedParams.TryGetValue(BaseAddress, out var addressValue) || addressValue.Text is not { } address || !IsHttpAddress(address))
            return new ConnectionTestResult(false, "The base address is not an http:// or https:// address.");

        var seconds = (double)DefaultTimeoutSeconds;
        if (resolvedParams.TryGetValue(Timeout, out var t) && t.Text is { Length: > 0 } text)
        {
            if (TimeoutSeconds(text) is not { } given || given is not (>= MinTimeoutSeconds and <= MaxTimeoutSeconds))
                return new ConnectionTestResult(false, TimeoutError(text));
            seconds = given;
        }
        var wait = TimeSpan.FromSeconds(Math.Min(seconds, MaxTestTime.TotalSeconds));
        using var client = new HttpClient(_handler(), disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        foreach (var (name, value) in resolvedParams.TryGetValue(Headers, out var h) ? h.Fields ?? [] : [])
            if (value.Text is { } headerValue && !request.Headers.TryAddWithoutValidation(name, headerValue))
                return new ConnectionTestResult(false, $"'{name}' can't be sent as a request header.");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(wait);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
            var status = $"{(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? new ConnectionTestResult(false, $"The server answered {status}: check the credentials in the headers.")
                : new ConnectionTestResult(true, $"The server answered {status}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionTestResult(false, $"The server did not answer within {wait.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds.");
        }
        catch (HttpRequestException ex)
        {
            return new ConnectionTestResult(false, $"Could not reach the server: {ex.Message}");
        }
    }

    // An HttpClient with the base address, headers and timeout set, so the script calls
    // Connections.Billing.GetStringAsync("invoices") and the like.
    public ConnectionClientCode CreateClient(MdRunConnection connection, ConnectionCodeContext context)
    {
        var parameters = connection.Params;
        var address = parameters[BaseAddress].Text!;
        var sb = new StringBuilder()
            .Append("/// <summary>The HTTP connection '").Append(ConnectionCodeContext.Doc(connection.Name)).Append("': an HttpClient for ")
            .Append(ConnectionCodeContext.Doc(address)).Append(".</summary>\n")
            .Append("public sealed class ").Append(context.ClassName).Append(" : global::System.Net.Http.HttpClient\n{\n")
            .Append("    internal ").Append(context.ClassName).Append("()\n    {\n")
            .Append("        BaseAddress = new global::System.Uri(").Append(context.Text(address)).Append(");\n");
        if (parameters.TryGetValue(Timeout, out var timeout) && timeout.Text is { Length: > 0 } seconds)
            sb.Append("        Timeout = global::System.TimeSpan.FromSeconds(global::System.Double.Parse(").Append(context.Text(seconds))
                .Append(", global::System.Globalization.CultureInfo.InvariantCulture));\n");
        foreach (var (name, value) in parameters.TryGetValue(Headers, out var headers) ? headers.Fields ?? [] : [])
            sb.Append("        DefaultRequestHeaders.TryAddWithoutValidation(").Append(ConnectionCodeContext.Literal(name)).Append(", ")
                .Append(context.Text(value.Text ?? "")).Append(");\n");
        sb.Append("    }\n}");
        return new ConnectionClientCode(sb.ToString());
    }

    private static bool HasPlaceholder(string text) => MdRunPlaceholders.Parse(text).Parts.Any(p => p.Kind != MdRunPlaceholders.Kind.Text);

    private static bool IsHttpAddress(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsRequestHeader(string name)
    {
        using var request = new HttpRequestMessage();
        return request.Headers.TryAddWithoutValidation(name, "x");
    }

    private static string TimeoutError(string seconds) =>
        $"The timeout '{seconds}' is not a number of seconds from {MinTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} to {MaxTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}.";

    private static double? TimeoutSeconds(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
}
