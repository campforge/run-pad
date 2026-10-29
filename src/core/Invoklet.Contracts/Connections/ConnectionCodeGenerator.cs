using System.Globalization;
using System.Text;
using Invoklet.Format.RPad;

namespace Invoklet.Contracts.Connections;

/// <summary>What a driver's <see cref="IConnectionDriver.CreateClient"/> writes code with.</summary>
public sealed class ConnectionCodeContext
{
    internal ConnectionCodeContext(RpadConnection connection, string className, ConnectionSchema? schema)
    {
        Connection = connection;
        ClassName = className;
        Schema = schema;
    }

    public RpadConnection Connection { get; }

    /// <summary>The endpoint's description, for an <see cref="IConnectionSchemaDriver"/>; null otherwise.</summary>
    public ConnectionSchema? Schema { get; }

    /// <summary>The client class name: normally the code name and <c>Client</c>, with a numeric suffix when needed to avoid another generated member.</summary>
    public string ClassName { get; }

    /// <summary>
    /// A C# string expression for connection text: literal parts as literals, <c>${secret:name}</c>
    /// as <c>Util.GetSecret("name")</c> and <c>${env:NAME}</c> as the variable, failing with a clear
    /// message when it isn't set. The text must have no placeholder errors (the generator checks).
    /// </summary>
    public string Text(string text)
    {
        var parts = RpadPlaceholders.Parse(text).Parts;
        if (parts.Count == 0) return "\"\"";
        return string.Join(" + ", parts.Select(part => part.Kind switch
        {
            RpadPlaceholders.Kind.Secret => $"global::Util.GetSecret({Literal(part.Value)})",
            RpadPlaceholders.Kind.Environment => $"(global::System.Environment.GetEnvironmentVariable({Literal(part.Value)}) ?? throw new global::System.InvalidOperationException({Literal($"Connection '{Connection.Name}': the environment variable '{part.Value}' is not set.")}))",
            _ => Literal(part.Value),
        }));
    }

    /// <summary>A C# string literal.</summary>
    public static string Literal(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c) || c is (char)0x2028 or (char)0x2029)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Text for an XML documentation comment.</summary>
    public static string Doc(string value)
    {
        // C# ends a line, and with it the /// comment, at U+0085, U+2028 and U+2029 too: a name read
        // from a database or a server must not be able to start a line of code.
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default:
                    sb.Append(char.IsControl(c) || c is (char)0x2028 or (char)0x2029 ? ' ' : c);
                    break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// The C# a script's <c>connections:</c> add to its build and to the editor: a static
/// <c>Connections</c> class with a property per connection (<c>Connections.Billing</c>), created on
/// first use, and each connection's client class nested in it (<c>Connections.BillingClient</c>).
/// </summary>
/// <param name="Files">C# files a connection's schema adds, such as a gRPC server's messages and clients.</param>
public sealed record GeneratedConnections(string Source, IReadOnlyList<string> Errors, IReadOnlyList<GeneratedSourceFile> Files)
{
    /// <summary>
    /// Null for a script without connections. Problems (an invalid list, a driver this Invoklet lacks,
    /// invalid parameters, a package the driver needs missing from <paramref name="scriptPackages"/>,
    /// a language without connections) are <see cref="Errors"/>, which fail the build; the valid
    /// connections still get their code, so the editor knows them.
    /// </summary>
    /// <param name="scriptPackages">The ids of the script's <c>packages:</c>.</param>
    /// <param name="schemas">The schemas read so far, for drivers that need one; such a connection
    /// without a schema is an error naming why it couldn't be read.</param>
    /// <param name="scriptDirectory">The script's folder, which a schema may depend on; null for a script never saved.</param>
    /// <param name="readSchemaFromDisk">Whether a missing in-memory schema may be loaded from disk.</param>
    public static GeneratedConnections? For(IReadOnlyList<RpadConnection> connections, RpadLanguage language,
        IEnumerable<string> scriptPackages, ConnectionDriverRegistry drivers, ConnectionSchemaCache? schemas = null,
        string? scriptDirectory = null, bool readSchemaFromDisk = true)
    {
        if (connections.Count == 0) return null;
        if (language != RpadLanguage.CSharp)
            return new GeneratedConnections("", ["Connections can be used in C# scripts only, for now."], []);
        var files = new List<GeneratedSourceFile>();
        var schemaFiles = new Dictionary<string, string>(StringComparer.Ordinal);

        var errors = RpadConnection.Validate(connections).ToList();
        var packages = new HashSet<string>(scriptPackages, StringComparer.OrdinalIgnoreCase);
        var classes = new StringBuilder();
        var properties = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var symbols = new HashSet<string>(connections.Select(c => RpadConnection.CodeName(c.Name)), StringComparer.Ordinal) { "Connections" };
        foreach (var connection in connections)
        {
            var codeName = RpadConnection.CodeName(connection.Name);
            if (codeName.Length == 0 || !char.IsAsciiLetter(codeName[0]) || !seen.Add(codeName)) continue; // reported by Validate
            if (drivers.Find(connection.Driver) is not { } driver)
            {
                if (!string.IsNullOrWhiteSpace(connection.Driver))
                    errors.Add($"Connection '{connection.Name}': this Invoklet has no '{connection.Driver}' driver.");
                continue;
            }
            var problems = driver.Validate(connection);
            if (problems.Count > 0 || connection.Params.Values.SelectMany(v => v.Texts()).Any(t => RpadPlaceholders.Parse(t).Error is not null))
            {
                errors.AddRange(problems.Select(p => $"Connection '{connection.Name}': {p}"));
                continue;
            }

            errors.AddRange(MissingPackages(driver, packages).Select(id =>
                $"Connection '{connection.Name}' needs the NuGet package {id}: add it in Dependencies → Packages."));

            if (codeName == "Connections")
            {
                errors.Add($"Connection '{connection.Name}': the code name Connections is reserved.");
                continue;
            }

            ConnectionSchema? schema = null;
            if (driver is IConnectionSchemaDriver)
            {
                schema = readSchemaFromDisk
                    ? schemas?.Get(connection, scriptDirectory)
                    : schemas?.TryGetLoaded(connection, scriptDirectory);
                if (schema is null)
                {
                    errors.Add($"Connection '{connection.Name}': " + (schemas?.ErrorFor(connection, scriptDirectory) is { } why
                        ? $"its description couldn't be read: {why}"
                        : "its description hasn't been read yet; Invoklet reads it when the script is opened or run."));
                    continue;
                }
                foreach (var file in schema.Files)
                {
                    if (schemaFiles.TryGetValue(file.Name, out var text))
                    {
                        if (text != file.Text)
                            errors.Add($"Connection '{connection.Name}': its schema file '{file.Name}' conflicts with another connection's schema.");
                        continue;
                    }
                    schemaFiles.Add(file.Name, file.Text);
                    files.Add(file with { Name = $"{codeName}.{file.Name}" });
                }
            }

            var className = codeName + "Client";
            for (var suffix = 2; !symbols.Add(className); suffix++) className = codeName + "Client" + suffix;
            var context = new ConnectionCodeContext(connection, className, schema);
            var client = driver.CreateClient(connection, context);
            classes.Append(Indent(client.ClassSource.Replace("\r\n", "\n").TrimEnd(), "    ")).Append("\n\n");
            var field = "__" + char.ToLowerInvariant(codeName[0]) + codeName[1..];
            properties
                .Append("    private static ").Append(context.ClassName).Append("? ").Append(field).Append(";\n")
                .Append("    /// <summary>The ").Append(ConnectionCodeContext.Doc(driver.DisplayName)).Append(" connection '")
                .Append(ConnectionCodeContext.Doc(connection.Name)).Append("' from the script's connections, created on first use.</summary>\n")
                .Append("    public static ").Append(context.ClassName).Append(' ').Append(codeName)
                .Append(" => ").Append(field).Append(" ??= new ").Append(context.ClassName).Append("();\n\n");
        }

        // The compiler treats a file named *.Generated.cs as generated code and switches nullable off in
        // it, so the annotations (T?) of the classes below need the directive written out (CS8669).
        var source = new StringBuilder()
            .Append("#nullable enable\n")
            .Append("/// <summary>The connections the script lists in its front matter (generated by Invoklet).</summary>\n")
            .Append("public static partial class Connections\n{\n")
            .Append(properties)
            .Append(classes)
            .Append("}\n")
            .Append("#nullable restore\n");
        return new GeneratedConnections(source.ToString(), errors, files);
    }

    /// <summary>The driver's packages that aren't among the script's.</summary>
    public static IEnumerable<string> MissingPackages(IConnectionDriver driver, IReadOnlySet<string> scriptPackages) =>
        driver.Packages.Keys.Where(id => !scriptPackages.Contains(id));

    private static string Indent(string text, string indent) =>
        string.Join("\n", text.Split('\n').Select(line => line.Length == 0 ? line : indent + line));
}
