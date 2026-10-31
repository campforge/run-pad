using System.Net.Http;
using System.Text;
using Invoklet.Contracts.Connections;

namespace Invoklet.Connectors.Http;

/// <summary>
/// The members the OpenAPI connection adds to its <c>HttpClient</c>: a class for each named schema and an <c>…Async</c> method for each operation,
/// so a script calls <c>Connections.Pets.GetPetAsync(7)</c> and gets a <c>Pet</c> back. Bodies and answers are JSON.
/// </summary>
internal static class OpenApiClientCode
{
    private static readonly string[] Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default",
        "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void",
        "volatile", "while",
    ];

    private const string Json = "global::System.Text.Json.JsonElement";

    public static string Members(OpenApiDocument document, string className)
    {
        // A name the generated class can't use for a member: those of HttpClient, its own helpers and itself.
        var taken = new HashSet<string>(typeof(HttpClient).GetMembers().Select(m => m.Name).Concat(["JsonOptions", "SendJsonAsync", "Fmt", "AddQuery", className]),
            StringComparer.Ordinal);

        var models = new Dictionary<string, (string Name, string? Alias)>(StringComparer.Ordinal);
        foreach (var model in document.Models)
            models[model.Name] = (model.AliasOf is null ? Unique(Pascal(model.Name, "Model"), taken, "Model") : "", model.AliasOf);

        var sb = new StringBuilder()
            .Append("    private static readonly global::System.Text.Json.JsonSerializerOptions JsonOptions = new(global::System.Text.Json.JsonSerializerDefaults.Web)\n")
            .Append("    {\n        DefaultIgnoreCondition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,\n    };\n\n")
            .Append("    private static string? Fmt(object? value) => value switch\n    {\n")
            .Append("        null => null,\n        string text => text,\n        bool flag => flag ? \"true\" : \"false\",\n")
            .Append("        global::System.DateTimeOffset moment => moment.ToString(\"O\", global::System.Globalization.CultureInfo.InvariantCulture),\n")
            .Append("        global::System.IFormattable formattable => formattable.ToString(null, global::System.Globalization.CultureInfo.InvariantCulture),\n")
            .Append("        global::System.Collections.IEnumerable items => string.Join(\",\", global::System.Linq.Enumerable.Select(global::System.Linq.Enumerable.Cast<object?>(items), Fmt)),\n")
            .Append("        _ => value.ToString(),\n    };\n\n")
            .Append("    private static void AddQuery(global::System.Collections.Generic.List<string> query, string name, object? value)\n    {\n")
            .Append("        if (Fmt(value) is { } text) query.Add(global::System.Uri.EscapeDataString(name) + \"=\" + global::System.Uri.EscapeDataString(text));\n    }\n\n")
            .Append("    private async global::System.Threading.Tasks.Task<string> SendJsonAsync(string method, string path, global::System.Collections.Generic.List<string> query,\n")
            .Append("        global::System.Collections.Generic.Dictionary<string, string?>? headers, object? body, global::System.Threading.CancellationToken cancellationToken)\n    {\n")
            .Append("        using var request = new global::System.Net.Http.HttpRequestMessage(new global::System.Net.Http.HttpMethod(method), query.Count == 0 ? path : path + \"?\" + string.Join(\"&\", query));\n")
            .Append("        if (headers is not null)\n            foreach (var header in headers)\n                if (header.Value is not null) request.Headers.TryAddWithoutValidation(header.Key, header.Value);\n")
            .Append("        if (body is not null)\n            request.Content = new global::System.Net.Http.StringContent(global::System.Text.Json.JsonSerializer.Serialize(body, body.GetType(), JsonOptions),\n")
            .Append("                global::System.Text.Encoding.UTF8, \"application/json\");\n")
            .Append("        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);\n")
            .Append("        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);\n")
            .Append("        if (!response.IsSuccessStatusCode)\n            throw new global::System.Net.Http.HttpRequestException($\"{(int)response.StatusCode} {response.ReasonPhrase}: {text}\", null, response.StatusCode);\n")
            .Append("        return text;\n    }\n");

        string CsType(string type, int depth = 0)
        {
            if (type.EndsWith("[]", StringComparison.Ordinal)) return "global::System.Collections.Generic.List<" + CsType(type[..^2], depth) + ">";
            switch (type)
            {
                case "string": return "string";
                case "string (date-time)": return "global::System.DateTimeOffset";
                case "string (date)": return "global::System.DateOnly";
                case "string (uuid)": return "global::System.Guid";
                case "string (binary)" or "string (byte)": return "byte[]";
                case "integer (int64)": return "long";
                case "number (float)": return "float";
                case "number (decimal)": return "decimal";
                case "boolean": return "bool";
                case "object" or "any": return Json;
            }
            if (type.StartsWith("string (", StringComparison.Ordinal)) return "string";
            if (type.StartsWith("integer", StringComparison.Ordinal)) return "int";
            if (type.StartsWith("number", StringComparison.Ordinal)) return "double";
            if (models.TryGetValue(type, out var model))
                return model.Alias is null ? model.Name : depth > 8 ? Json : CsType(model.Alias, depth + 1);
            return Json;
        }

        static bool IsValueType(string cs) =>
            cs is "bool" or "int" or "long" or "float" or "double" or "decimal" or Json or "global::System.DateTimeOffset" or "global::System.DateOnly" or "global::System.Guid";

        // The operations.
        var usedNames = new HashSet<string>(taken, StringComparer.Ordinal);
        foreach (var model in document.Models.Where(m => m.AliasOf is null)) usedNames.Add(models[model.Name].Name);
        foreach (var operation in document.Operations)
        {
            var name = Unique(Pascal(operation.OperationId ?? OperationName(operation), "Operation") + "Async", usedNames, "Operation");
            var names = new HashSet<string>(["cancellationToken", "query", "headers", "text"], StringComparer.Ordinal);
            var arguments = new List<(string Declaration, bool Required, string Name, OpenApiParameter? Parameter)>();
            foreach (var parameter in operation.Parameters.Where(p => p.In is "path" or "query" or "header"))
            {
                var cs = CsType(parameter.Type);
                var local = Unique(Camel(parameter.Name), names, "Value");
                var required = parameter.Required || parameter.In == "path";
                arguments.Add(($"{cs}{(required ? "" : "?")} {local}{(required ? "" : " = default")}", required, local, parameter));
            }
            string? bodyName = null;
            if (operation.BodyType is { } bodyType)
            {
                bodyName = Unique("body", names, "Value");
                arguments.Add(($"{CsType(bodyType)} {bodyName}", true, bodyName, null));
            }
            var signature = arguments.Where(a => a.Required).Concat(arguments.Where(a => !a.Required)).Select(a => a.Declaration)
                .Append("global::System.Threading.CancellationToken cancellationToken = default");

            var path = new List<string>();
            var template = operation.Path.TrimStart('/');
            for (var i = 0; i < template.Length;)
            {
                var open = template.IndexOf('{', i);
                var close = open < 0 ? -1 : template.IndexOf('}', open);
                if (close < 0) { path.Add(ConnectionCodeContext.Literal(template[i..])); break; }
                if (open > i) path.Add(ConnectionCodeContext.Literal(template[i..open]));
                var placeholder = template[(open + 1)..close];
                var argument = arguments.FirstOrDefault(a => a.Parameter is { In: "path" } p && p.Name == placeholder);
                path.Add(argument.Name is null ? ConnectionCodeContext.Literal(template[open..(close + 1)])
                    : $"global::System.Uri.EscapeDataString(Fmt({argument.Name}) ?? \"\")");
                i = close + 1;
            }
            if (path.Count == 0) path.Add("\"\"");

            var returns = operation.ResponseType is { } responseType ? CsType(responseType) : null;
            sb.Append('\n').Append("    /// <summary>").Append(ConnectionCodeContext.Doc(operation.Summary ?? operation.OperationId ?? "")).Append(" (")
                .Append(operation.Method).Append(' ').Append(ConnectionCodeContext.Doc(operation.Path)).Append(").</summary>\n")
                .Append("    public async global::System.Threading.Tasks.Task").Append(returns is null ? "" : "<" + returns + ">").Append(' ').Append(name)
                .Append('(').Append(string.Join(", ", signature)).Append(")\n    {\n")
                .Append("        var query = new global::System.Collections.Generic.List<string>();\n");
            foreach (var argument in arguments.Where(a => a.Parameter is { In: "query" }))
                sb.Append("        AddQuery(query, ").Append(ConnectionCodeContext.Literal(argument.Parameter!.Name)).Append(", ").Append(argument.Name).Append(");\n");
            var headers = arguments.Where(a => a.Parameter is { In: "header" }).ToList();
            if (headers.Count > 0)
            {
                sb.Append("        var headers = new global::System.Collections.Generic.Dictionary<string, string?>();\n");
                foreach (var argument in headers)
                    sb.Append("        headers[").Append(ConnectionCodeContext.Literal(argument.Parameter!.Name)).Append("] = Fmt(").Append(argument.Name).Append(");\n");
            }
            sb.Append("        ").Append(returns is null ? "" : "var text = ").Append("await SendJsonAsync(").Append(ConnectionCodeContext.Literal(operation.Method)).Append(", ")
                .Append(string.Join(" + ", path)).Append(", query, ").Append(headers.Count > 0 ? "headers" : "null").Append(", ").Append(bodyName ?? "null")
                .Append(", cancellationToken).ConfigureAwait(false);\n");
            if (returns is not null)
                sb.Append("        return text.Length == 0 ? default! : global::System.Text.Json.JsonSerializer.Deserialize<").Append(returns).Append(">(text, JsonOptions)!;\n");
            sb.Append("    }\n");
        }

        // The types.
        foreach (var model in document.Models.Where(m => m.AliasOf is null))
        {
            var className2 = models[model.Name].Name;
            var members = new HashSet<string>(["Equals", "GetHashCode", "GetType", "MemberwiseClone", "ToString", className2], StringComparer.Ordinal);
            sb.Append('\n').Append("    /// <summary>The schema ").Append(ConnectionCodeContext.Doc(model.Name)).Append(".</summary>\n")
                .Append("    public sealed class ").Append(className2).Append("\n    {\n");
            foreach (var property in model.Properties.DistinctBy(p => p.Name))
            {
                var cs = CsType(property.Type);
                sb.Append("        [global::System.Text.Json.Serialization.JsonPropertyName(").Append(ConnectionCodeContext.Literal(property.Name)).Append(")]\n")
                    .Append("        public ").Append(cs).Append(property.Required ? "" : "?").Append(' ').Append(Unique(Pascal(property.Name, "Value"), members, "Value"))
                    .Append(" { get; set; }").Append(property.Required && !IsValueType(cs) ? " = default!;" : "").Append('\n');
            }
            sb.Append("    }\n");
        }
        return sb.ToString();
    }

    // getPets for GET /pets, getPetsById for GET /pets/{id}.
    private static string OperationName(OpenApiOperation operation) =>
        operation.Method.ToLowerInvariant() + string.Concat(operation.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.StartsWith('{') ? "By" + Pascal(part.Trim('{', '}'), "Id") : Pascal(part, "Path")));

    private static string Pascal(string name, string fallback)
    {
        var sb = new StringBuilder(name.Length);
        var upper = true;
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c)) { upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        if (sb.Length == 0) return fallback;
        if (char.IsDigit(sb[0])) sb.Insert(0, fallback);
        return sb.ToString();
    }

    private static string Camel(string name)
    {
        var pascal = Pascal(name, "value");
        var camel = char.ToLowerInvariant(pascal[0]) + pascal[1..];
        return Keywords.Contains(camel) ? "@" + camel : camel;
    }

    private static string Unique(string name, HashSet<string> taken, string suffix)
    {
        if (taken.Add(name)) return name;
        var candidate = name + suffix;
        for (var i = 2; !taken.Add(candidate); i++) candidate = name + suffix + i;
        return candidate;
    }
}
