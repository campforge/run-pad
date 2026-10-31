using System.Net;
using System.Text;
using Invoklet.Connectors.Http;
using Invoklet.Contracts.Connections;
using Invoklet.Format.MdRun;
using Xunit;

namespace Invoklet.NonUI.Tests.Connectors.Http;

// An OpenAPI connection has the parameters of an HTTP one and nothing more; its document is looked for at the usual addresses under the base address.
public class OpenApiConnectionDriverTests
{
    private static readonly ConnectionTools Tools = new() { GetPackageFolderAsync = (_, _, _) => Task.FromResult("") };

    private const string Spec = """
        {
          "openapi": "3.0.1",
          "paths": {
            "/pets": {
              "get": {
                "operationId": "listPets", "summary": "All pets",
                "parameters": [ { "name": "limit", "in": "query", "schema": { "type": "integer" } }, { "name": "X-Trace", "in": "header", "schema": { "type": "string" } } ],
                "responses": { "200": { "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Pet" } } } } } }
              },
              "post": {
                "operationId": "addPet",
                "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } } },
                "responses": { "201": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } } } }
              }
            },
            "/pets/{id}": {
              "delete": { "operationId": "removePet", "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "integer", "format": "int64" } } ], "responses": { "204": { } } }
            }
          },
          "components": { "schemas": {
            "Pet": { "type": "object", "required": ["id", "name"], "properties": { "id": { "type": "integer", "format": "int64" }, "name": { "type": "string" }, "kind": { "$ref": "#/components/schemas/Kind" }, "born": { "type": "string", "format": "date-time" } } },
            "Kind": { "type": "string", "enum": ["cat", "dog"] }
          } }
        }
        """;

    private sealed class RecordingHandler(Func<Uri, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (status, body) = answer(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static Dictionary<string, MdRunValue> Params(string baseAddress = "https://api.local/v1/") =>
        new(StringComparer.Ordinal)
        {
            ["baseAddress"] = MdRunValue.Of(baseAddress),
            ["headers"] = MdRunValue.Map([new("Authorization", MdRunValue.Of("Bearer t0k"))]),
        };

    [Fact]
    public void Params_AreExactlyThoseOfAnHttpConnection()
    {
        var sut = new OpenApiConnectionDriver();

        Assert.Equal(new HttpConnectionDriver().Params.Select(p => p.Key), sut.Params.Select(p => p.Key));
        Assert.Equal("OpenAPI/Swagger", sut.DisplayName);
    }

    [Fact]
    public async Task FetchSchemaAsync_TheDocumentIsNotWhereTheFirstGuessIs_TriesTheNextAddressWithTheHeaders()
    {
        var handler = new RecordingHandler(uri => uri.AbsolutePath == "/v1/swagger.json" ? (HttpStatusCode.OK, Spec) : (HttpStatusCode.NotFound, ""));
        var sut = new OpenApiConnectionDriver(() => handler);

        var schema = await sut.FetchSchemaAsync(Params(), Tools, TestContext.Current.CancellationToken);

        Assert.Equal(["https://api.local/v1/openapi.json", "https://api.local/v1/swagger.json"], handler.Requests.Select(r => r.RequestUri!.ToString()));
        Assert.All(handler.Requests, r => Assert.Equal("Bearer t0k", r.Headers.Authorization!.ToString()));
        Assert.Equal("3 operations.", schema.Summary);
    }

    [Fact]
    public async Task FetchSchemaAsync_NothingAnswersWithADocument_NamesWhatWasTried()
    {
        var sut = new OpenApiConnectionDriver(() => new RecordingHandler(_ => (HttpStatusCode.NotFound, "")));

        var thrown = await Assert.ThrowsAsync<OpenApiException>(() => sut.FetchSchemaAsync(Params(), Tools, TestContext.Current.CancellationToken));

        Assert.StartsWith("No OpenAPI or Swagger document (JSON) found under https://api.local/v1/: tried openapi.json, swagger.json, swagger/v1/swagger.json", thrown.Message);
    }

    [Fact]
    public async Task FetchSchemaAsync_TheServerRefusesTheCredentials_SaysSo()
    {
        var sut = new OpenApiConnectionDriver(() => new RecordingHandler(_ => (HttpStatusCode.Unauthorized, "")));

        var thrown = await Assert.ThrowsAsync<OpenApiException>(() => sut.FetchSchemaAsync(Params(), Tools, TestContext.Current.CancellationToken));

        Assert.Equal("The server answered 401 for https://api.local/v1/openapi.json: check the credentials in the headers.", thrown.Message);
    }

    [Fact]
    public void Members_ADocument_BecomeATypedClientWithAClassPerSchema()
    {
        // Arrange
        var document = OpenApiReader.Read(Spec);

        // Act
        var code = OpenApiClientCode.Members(document, "PetsClient");

        // Assert
        Assert.Contains("public async global::System.Threading.Tasks.Task<global::System.Collections.Generic.List<Pet>> ListPetsAsync(int? limit = default, string? xTrace = default, global::System.Threading.CancellationToken cancellationToken = default)", code, StringComparison.Ordinal);
        Assert.Contains("AddQuery(query, \"limit\", limit);", code);
        Assert.Contains("headers[\"X-Trace\"] = Fmt(xTrace);", code);
        Assert.Contains("Task<Pet> AddPetAsync(Pet body, global::System.Threading.CancellationToken cancellationToken = default)", code);
        Assert.Contains("public async global::System.Threading.Tasks.Task RemovePetAsync(long id, ", code);
        Assert.Contains("\"pets/\" + global::System.Uri.EscapeDataString(Fmt(id) ?? \"\")", code);
        Assert.Contains("public sealed class Pet", code);
        Assert.Contains("public string Name { get; set; } = default!;", code);
        Assert.Contains("public string? Kind { get; set; }", code);
        Assert.Contains("public global::System.DateTimeOffset? Born { get; set; }", code);
        Assert.DoesNotContain("class Kind", code);
    }
}
