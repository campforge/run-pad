using Invoklet.Connectors.Http;
using Xunit;

namespace Invoklet.NonUI.Tests.Connectors.Http;

public class OpenApiReaderTests
{
    private const string OpenApi3 = """
        {
          "openapi": "3.0.1",
          "paths": {
            "/pets": {
              "parameters": [ { "name": "trace", "in": "header", "schema": { "type": "string" } } ],
              "get": {
                "tags": ["pets"], "operationId": "listPets", "summary": "All pets",
                "parameters": [ { "name": "limit", "in": "query", "required": true, "schema": { "type": "integer", "format": "int32" } } ],
                "responses": { "200": { "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Pet" } } } } }, "404": { } }
              },
              "post": {
                "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } } },
                "responses": { "201": { } }
              }
            },
            "/pets/{id}": {
              "delete": { "parameters": [ { "$ref": "#/components/parameters/PetId" } ], "responses": { "204": { } } }
            }
          },
          "components": { "parameters": { "PetId": { "name": "id", "in": "path", "required": true, "schema": { "type": "string", "format": "uuid" } } } }
        }
        """;

    [Fact]
    public void Read_AnOpenApi3Document_ListsOperationsWithParametersBodiesAndAnswers()
    {
        // Arrange / Act
        var operations = OpenApiReader.Read(OpenApi3);

        // Assert
        Assert.Equal(["GET /pets", "POST /pets", "DELETE /pets/{id}"], operations.Select(o => $"{o.Method} {o.Path}"));
        var list = operations[0];
        Assert.Equal("pets", list.Tag);
        Assert.Equal("All pets", list.Summary);
        Assert.Equal([("limit", "query", "integer (int32)", true), ("trace", "header", "string", false)],
            list.Parameters.OrderBy(p => p.Name).Select(p => (p.Name, p.In, p.Type, p.Required)));
        Assert.Equal(["200: Pet[]", "404"], list.Responses);
        Assert.Equal("application/json: Pet", operations[1].RequestBody);
        var delete = Assert.Single(operations[2].Parameters);
        Assert.Equal(("id", "path", "string (uuid)", true), (delete.Name, delete.In, delete.Type, delete.Required));
    }

    [Fact]
    public void Read_ASwagger2Document_TakesTheBodyParameterAsTheRequestBody()
    {
        const string swagger = """
            { "swagger": "2.0", "paths": { "/pets": { "post": { "parameters": [
                { "name": "pet", "in": "body", "schema": { "$ref": "#/definitions/Pet" } },
                { "name": "dry", "in": "query", "type": "boolean" } ],
              "responses": { "200": { "schema": { "$ref": "#/definitions/Pet" } } } } } } }
            """;

        var operation = Assert.Single(OpenApiReader.Read(swagger));

        Assert.Equal("body: Pet", operation.RequestBody);
        Assert.Equal(["dry"], operation.Parameters.Select(p => p.Name));
        Assert.Equal(["200: Pet"], operation.Responses);
    }

    [Theory]
    [InlineData("openapi: 3.0.1\npaths: {}", "The OpenAPI document is not JSON: Invoklet reads the JSON form (openapi.json), not YAML.")]
    [InlineData("{ \"info\": {} }", "The document has no \"paths\": it is not an OpenAPI or Swagger description.")]
    public void Read_ADocumentThatIsNotOpenApiJson_SaysWhy(string document, string message)
    {
        var thrown = Assert.Throws<OpenApiException>(() => OpenApiReader.Read(document));

        Assert.Equal(message, thrown.Message);
    }
}
