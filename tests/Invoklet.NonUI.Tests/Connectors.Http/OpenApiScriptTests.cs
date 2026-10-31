using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Invoklet.Connectors.Http;
using Invoklet.Contracts.Connections;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests.Connectors.Http;

// The generated client is compiled and run against a server on this machine that serves a document and a few operations.
public sealed class OpenApiScriptTests : IDisposable
{
    private const string Spec = """
        {
          "openapi": "3.0.1",
          "paths": {
            "/pets": {
              "get": { "operationId": "listPets", "parameters": [ { "name": "kind", "in": "query", "schema": { "type": "string" } } ],
                       "responses": { "200": { "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Pet" } } } } } } },
              "post": { "operationId": "addPet", "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } } },
                        "responses": { "201": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } } } } }
            },
            "/pets/{id}": { "delete": { "operationId": "removePet", "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "integer", "format": "int64" } } ], "responses": { "204": { } } } }
          },
          "components": { "schemas": { "Pet": { "type": "object", "required": ["name"], "properties": { "id": { "type": "integer", "format": "int64" }, "name": { "type": "string" } } } } }
        }
        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("Invoklet-openapi-script").FullName;
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<string> _seen = [];
    private readonly string _address;

    public OpenApiScriptTests()
    {
        // A free port: the listener is opened on the first one that accepts it.
        for (var port = 41000; ; port++)
        {
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/api/");
                _listener.Start();
                _address = $"http://127.0.0.1:{port}/api/";
                break;
            }
            catch (HttpListenerException) when (port < 41100) { }
        }
        _ = Task.Run(Serve);
    }

    public void Dispose()
    {
        _listener.Close();
        Directory.Delete(_folder, true);
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            var request = context.Request;
            var body = request.HasEntityBody ? await new StreamReader(request.InputStream).ReadToEndAsync() : "";
            _seen.Enqueue($"{request.HttpMethod} {request.Url!.PathAndQuery} {body}".TrimEnd());
            var (status, text) = (request.HttpMethod, request.Url.AbsolutePath) switch
            {
                ("GET", "/api/openapi.json") => (200, Spec),
                ("GET", "/api/pets") => (200, "[{\"id\":1,\"name\":\"Rex\"},{\"id\":2,\"name\":\"Tom\"}]"),
                ("POST", "/api/pets") => (201, "{\"id\":3,\"name\":\"New\"}"),
                ("DELETE", "/api/pets/7") => (204, ""),
                _ => (404, "no"),
            };
            context.Response.StatusCode = status;
            var bytes = Encoding.UTF8.GetBytes(text);
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    [Fact]
    [Trait("Category", "RealBuild")]
    public async Task AScript_Should_CallTheOperationsOfItsConnection_AsTypedMethods()
    {
        if (!new DotnetSdkProvider().Get().Any()) return;
        var connection = new MdRunConnection
        {
            Name = "pets",
            Driver = "openapi",
            Params = new(StringComparer.Ordinal) { ["baseAddress"] = MdRunValue.Of(_address) },
        };
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new OpenApiConnectionDriver());
        var schemas = new ConnectionSchemas(drivers, Path.Combine(_folder, "schemas"), DotnetConnectionTools.Default, _ => null);
        Assert.True(await schemas.EnsureAsync([connection], null, ConnectionSchemaRefresh.Missing, TestContext.Current.CancellationToken));
        Assert.Null(schemas.Cache.ErrorFor(connection));

        var generated = GeneratedConnections.For([connection], MdRunLanguage.CSharp, [], drivers, schemas.Cache)!;
        Assert.Empty(generated.Errors);

        var outputDir = Path.Combine(_folder, "build");
        Directory.CreateDirectory(outputDir);
        var options = CompiledDotnetLanguageHostOptions.From(new ExecutionRequest
        {
            Platform = new DotnetSdkProvider().Get().First(),
            OutputDirectory = outputDir,
            WorkingDirectory = outputDir,
            Connections = generated,
        }, new CSharpReferenceService().GetExtensionReference());
        await using var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance);
        var output = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        const string script = """
            var pets = Connections.Pets;
            var all = await pets.ListPetsAsync("dog");
            Console.WriteLine(string.Join(",", all.Select(p => p.Name)));
            var added = await pets.AddPetAsync(new Connections.PetsClient.Pet { Name = "New" });
            Console.WriteLine(added.Id);
            await pets.RemovePetAsync(7);
            try { await pets.RemovePetAsync(8); } catch (HttpRequestException ex) { Console.WriteLine(ex.StatusCode); }
            """;
        var result = await host.RunAsync(script, (data, kind) => (kind == OutputKind.StandardError ? errors : output).Enqueue(data),
                TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, string.Join('\n', errors));
        Assert.Equal(["Rex,Tom", "3", "NotFound"], output.TakeLast(3));
        Assert.Contains("GET /api/pets?kind=dog", _seen);
        Assert.Contains(_seen, s => s.StartsWith("POST /api/pets {", StringComparison.Ordinal) && s.Contains("\"name\":\"New\"") && !s.Contains("\"id\""));
        Assert.Contains("DELETE /api/pets/7", _seen);
    }
}
