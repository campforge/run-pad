using System.Text.Json;

namespace Invoklet.Connectors.Http;

/// <summary>What Invoklet keeps of an OpenAPI (or Swagger 2) document: the operations to call and the types they send and receive.</summary>
internal sealed record OpenApiDocument(List<OpenApiOperation> Operations, List<OpenApiModel> Models);

/// <summary>One operation: a method on a path.</summary>
internal sealed record OpenApiOperation(string Method, string Path, string? OperationId, string? Summary, string? Tag)
{
    public List<OpenApiParameter> Parameters { get; init; } = [];

    /// <summary>The body a request sends, as "media type: type", for showing; null for none.</summary>
    public string? RequestBody { get; init; }

    /// <summary>The type of the body a request sends as JSON; null for none.</summary>
    public string? BodyType { get; init; }

    /// <summary>The answers, as "status: type" (or just the status when it has no body), for showing.</summary>
    public List<string> Responses { get; init; } = [];

    /// <summary>The type of the first successful answer's body; null when none has one.</summary>
    public string? ResponseType { get; init; }
}

/// <param name="In">path, query, header or cookie.</param>
/// <param name="Type">A type as <see cref="OpenApiReader"/> names it: <c>Pet</c>, <c>Pet[]</c>, <c>integer (int64)</c>, <c>string</c>, <c>object</c>.</param>
internal sealed record OpenApiParameter(string Name, string In, string Type, bool Required);

/// <summary>A named schema: an object with properties, or (<paramref name="AliasOf"/>) another type under a name, as an enum is a string.</summary>
internal sealed record OpenApiModel(string Name, string? AliasOf, List<OpenApiProperty> Properties);

internal sealed record OpenApiProperty(string Name, string Type, bool Required);

/// <summary>
/// Reads an OpenAPI 3 or Swagger 2 document in JSON: the path, method, parameters, request body and answers of each operation, grouped by their
/// first tag, and the named schemas they use. A type is named by what it is (<c>integer (int64)</c>, <c>Pet[]</c>) so that the browser can show it
/// and the code generator can turn it into C#.
/// </summary>
internal static class OpenApiReader
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    public static OpenApiDocument Read(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException)
        {
            throw new OpenApiException("The OpenAPI document is not JSON: Invoklet reads the JSON form (openapi.json), not YAML.");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
                throw new OpenApiException("The document has no \"paths\": it is not an OpenAPI or Swagger description.");

            var operations = new List<OpenApiOperation>();
            foreach (var path in paths.EnumerateObject())
            {
                if (path.Value.ValueKind != JsonValueKind.Object) continue;
                var shared = Parameters(root, path.Value).ToList();
                foreach (var method in Methods)
                {
                    if (!path.Value.TryGetProperty(method, out var operation) || operation.ValueKind != JsonValueKind.Object) continue;
                    var own = Parameters(root, operation).ToList();
                    // An operation's own parameter replaces a shared one of the same name and place.
                    var parameters = shared.Where(s => !own.Any(o => o.Name == s.Name && o.In == s.In)).Concat(own).ToList();
                    var body = parameters.FirstOrDefault(p => p.In == "body");
                    parameters.RemoveAll(p => p.In == "body");
                    var (requestBody, bodyType) = RequestBody(root, operation);
                    if (requestBody is null && body is not null) (requestBody, bodyType) = ("body: " + body.Type, body.Type);
                    var (responses, responseType) = Responses(operation);
                    operations.Add(new OpenApiOperation(method.ToUpperInvariant(), path.Name, Text(operation, "operationId"), Text(operation, "summary") ?? Text(operation, "description"),
                        operation.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.GetArrayLength() > 0 ? tags[0].GetString() : null)
                    {
                        Parameters = parameters,
                        RequestBody = requestBody,
                        BodyType = bodyType,
                        Responses = responses,
                        ResponseType = responseType,
                    });
                }
            }
            return new OpenApiDocument(operations, Models(root));
        }
    }

    private static List<OpenApiModel> Models(JsonElement root)
    {
        var schemas = root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Object
            && components.TryGetProperty("schemas", out var s) ? s
            : root.TryGetProperty("definitions", out var definitions) ? definitions : default;
        var models = new List<OpenApiModel>();
        if (schemas.ValueKind != JsonValueKind.Object) return models;
        foreach (var schema in schemas.EnumerateObject())
        {
            if (schema.Value.ValueKind != JsonValueKind.Object) continue;
            var properties = ModelProperties(root, schema.Value, 0).ToList();
            var type = Text(schema.Value, "type");
            // An enum, an array or a plain value under a name is that type; an object, or one built with allOf, is a class.
            models.Add(properties.Count == 0 && type is not (null or "object") || schema.Value.TryGetProperty("enum", out _)
                ? new OpenApiModel(schema.Name, schema.Value.TryGetProperty("enum", out _) && type is null ? "string" : TypeOf(schema.Value), [])
                : new OpenApiModel(schema.Name, null, properties));
        }
        return models;
    }

    // allOf is the properties of each part, a reference followed; oneOf and anyOf are left as an untyped value.
    private static IEnumerable<OpenApiProperty> ModelProperties(JsonElement root, JsonElement schema, int depth)
    {
        if (depth > 8) yield break;
        schema = Resolve(root, schema);
        if (schema.TryGetProperty("allOf", out var all) && all.ValueKind == JsonValueKind.Array)
            foreach (var part in all.EnumerateArray())
                foreach (var property in ModelProperties(root, part, depth + 1))
                    yield return property;
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) yield break;
        var required = schema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).ToHashSet() : [];
        foreach (var property in properties.EnumerateObject())
            yield return new OpenApiProperty(property.Name, TypeOf(property.Value), required.Contains(property.Name));
    }

    private static IEnumerable<OpenApiParameter> Parameters(JsonElement root, JsonElement owner)
    {
        if (!owner.TryGetProperty("parameters", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in list.EnumerateArray())
        {
            var parameter = Resolve(root, item);
            if (Text(parameter, "name") is not { } name) continue;
            var type = parameter.TryGetProperty("schema", out var schema) ? TypeOf(schema) : TypeOf(parameter);
            yield return new OpenApiParameter(name, Text(parameter, "in") ?? "query", type,
                parameter.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True);
        }
    }

    private static (string? Shown, string? Type) RequestBody(JsonElement root, JsonElement operation)
    {
        if (!operation.TryGetProperty("requestBody", out var body)) return (null, null);
        body = Resolve(root, body);
        if (!body.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object) return (null, null);
        foreach (var media in content.EnumerateObject())
        {
            var type = media.Value.TryGetProperty("schema", out var schema) ? TypeOf(schema) : null;
            return (media.Name + (type is null ? "" : ": " + type), media.Name.Contains("json", StringComparison.OrdinalIgnoreCase) ? type : null);
        }
        return (null, null);
    }

    private static (List<string> Shown, string? SuccessType) Responses(JsonElement operation)
    {
        var shown = new List<string>();
        string? success = null;
        if (!operation.TryGetProperty("responses", out var responses) || responses.ValueKind != JsonValueKind.Object) return (shown, null);
        foreach (var response in responses.EnumerateObject())
        {
            var type = response.Value.ValueKind != JsonValueKind.Object ? null
                : response.Value.TryGetProperty("schema", out var swagger) ? TypeOf(swagger)
                : response.Value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
                  && content.EnumerateObject().FirstOrDefault().Value is { ValueKind: JsonValueKind.Object } first && first.TryGetProperty("schema", out var schema)
                    ? TypeOf(schema) : null;
            shown.Add(type is null ? response.Name : response.Name + ": " + type);
            if (success is null && type is not null && response.Name.StartsWith('2')) success = type;
        }
        return (shown, success);
    }

    // A local reference ("#/components/parameters/Id") is followed once; anything else stays as it is.
    private static JsonElement Resolve(JsonElement root, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || Text(element, "$ref") is not { } reference || !reference.StartsWith("#/", StringComparison.Ordinal))
            return element;
        var current = root;
        foreach (var part in reference[2..].Split('/'))
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part.Replace("~1", "/").Replace("~0", "~"), out current)) return element;
        return current;
    }

    private static string TypeOf(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return "any";
        if (Text(schema, "$ref") is { } reference) return reference[(reference.LastIndexOf('/') + 1)..];
        var type = Text(schema, "type");
        if (type == "array") return (schema.TryGetProperty("items", out var items) ? TypeOf(items) : "any") + "[]";
        if (type is null) return schema.TryGetProperty("properties", out _) ? "object" : "any";
        return Text(schema, "format") is { } format ? $"{type} ({format})" : type;
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A document that can't be read, in words for the user.</summary>
internal sealed class OpenApiException(string message) : Exception(message);
