using System.Text.Json;

namespace Invoklet.Connectors.Http;

/// <summary>One operation of an OpenAPI (or Swagger 2) document: a method on a path.</summary>
internal sealed record OpenApiOperation(string Method, string Path, string? OperationId, string? Summary, string? Tag)
{
    public List<OpenApiParameter> Parameters { get; init; } = [];

    /// <summary>The body a request sends, as "media type: type"; null for none.</summary>
    public string? RequestBody { get; init; }

    /// <summary>The answers, as "status: type" (or just the status when it has no body).</summary>
    public List<string> Responses { get; init; } = [];
}

/// <param name="In">path, query, header or cookie.</param>
internal sealed record OpenApiParameter(string Name, string In, string Type, bool Required);

/// <summary>
/// Reads the operations out of an OpenAPI 3 or Swagger 2 document in JSON: the path, method, parameters, request body and answers of each,
/// grouped by their first tag. Only what the browser shows is kept; a schema is named by its type or the name it is referenced by.
/// </summary>
internal static class OpenApiReader
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    public static List<OpenApiOperation> Read(string json)
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
                    operations.Add(new OpenApiOperation(method.ToUpperInvariant(), path.Name, Text(operation, "operationId"), Text(operation, "summary") ?? Text(operation, "description"),
                        operation.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.GetArrayLength() > 0 ? tags[0].GetString() : null)
                    {
                        Parameters = parameters,
                        RequestBody = RequestBody(root, operation) ?? (body is null ? null : "body: " + body.Type),
                        Responses = Responses(operation),
                    });
                }
            }
            return operations;
        }
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

    private static string? RequestBody(JsonElement root, JsonElement operation)
    {
        if (!operation.TryGetProperty("requestBody", out var body)) return null;
        body = Resolve(root, body);
        if (!body.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object) return null;
        foreach (var media in content.EnumerateObject())
            return media.Name + (media.Value.TryGetProperty("schema", out var schema) ? ": " + TypeOf(schema) : "");
        return null;
    }

    private static List<string> Responses(JsonElement operation)
    {
        var result = new List<string>();
        if (!operation.TryGetProperty("responses", out var responses) || responses.ValueKind != JsonValueKind.Object) return result;
        foreach (var response in responses.EnumerateObject())
        {
            var type = response.Value.ValueKind != JsonValueKind.Object ? null
                : response.Value.TryGetProperty("schema", out var swagger) ? TypeOf(swagger)
                : response.Value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
                  && content.EnumerateObject().FirstOrDefault().Value is { ValueKind: JsonValueKind.Object } first && first.TryGetProperty("schema", out var schema)
                    ? TypeOf(schema) : null;
            result.Add(type is null ? response.Name : response.Name + ": " + type);
        }
        return result;
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
