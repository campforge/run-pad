using System.Globalization;
using System.Text;
using RunPad.Format.RPad;

namespace RunPad.Contracts.Connections;

/// <summary>What a driver's <see cref="IConnectionDriver.CreateClient"/> writes code with.</summary>
public sealed class ConnectionCodeContext
{
    internal ConnectionCodeContext(RpadConnection connection) => Connection = connection;

    public RpadConnection Connection { get; }

    /// <summary>The client class's name: the connection's code name and <c>Client</c>, <c>BillingClient</c> for <c>billing</c>.</summary>
    public string ClassName => RpadConnection.CodeName(Connection.Name) + "Client";

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
    public static string Doc(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", " ").Replace("\n", " ");
}

/// <summary>
/// The C# a script's <c>connections:</c> add to its build and to the editor: a static
/// <c>Connections</c> class with a property per connection (<c>Connections.Billing</c>), created on
/// first use, and each connection's client class nested in it (<c>Connections.BillingClient</c>).
/// </summary>
public sealed record GeneratedConnections(string Source, IReadOnlyDictionary<string, string> Packages, IReadOnlyList<string> Errors)
{
    /// <summary>
    /// Null for a script without connections. Problems (an invalid list, a driver this RunPad lacks,
    /// invalid parameters, a language without connections) are <see cref="Errors"/>, which fail the
    /// build; the valid connections still get their code, so the editor knows them.
    /// </summary>
    public static GeneratedConnections? For(IReadOnlyList<RpadConnection> connections, RpadLanguage language, ConnectionDriverRegistry drivers)
    {
        if (connections.Count == 0) return null;
        if (language != RpadLanguage.CSharp)
            return new GeneratedConnections("", new Dictionary<string, string>(), ["Connections can be used in C# scripts only, for now."]);

        var errors = RpadConnection.Validate(connections).ToList();
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var classes = new StringBuilder();
        var properties = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in connections)
        {
            var codeName = RpadConnection.CodeName(connection.Name);
            if (codeName.Length == 0 || !char.IsAsciiLetter(codeName[0]) || !seen.Add(codeName)) continue; // reported by Validate
            if (drivers.Find(connection.Driver) is not { } driver)
            {
                if (!string.IsNullOrWhiteSpace(connection.Driver))
                    errors.Add($"Connection '{connection.Name}': this RunPad has no '{connection.Driver}' driver.");
                continue;
            }
            var problems = driver.Validate(connection);
            if (problems.Count > 0 || connection.Params.Values.SelectMany(v => v.Texts()).Any(t => RpadPlaceholders.Parse(t).Error is not null))
            {
                errors.AddRange(problems.Select(p => $"Connection '{connection.Name}': {p}"));
                continue;
            }

            var context = new ConnectionCodeContext(connection);
            var client = driver.CreateClient(connection, context);
            foreach (var (id, version) in client.Packages ?? new Dictionary<string, string>()) packages[id] = version;
            classes.Append(Indent(client.ClassSource.Replace("\r\n", "\n").TrimEnd(), "    ")).Append("\n\n");
            var field = "__" + char.ToLowerInvariant(codeName[0]) + codeName[1..];
            properties
                .Append("    private static ").Append(context.ClassName).Append("? ").Append(field).Append(";\n")
                .Append("    /// <summary>The ").Append(ConnectionCodeContext.Doc(driver.DisplayName)).Append(" connection '")
                .Append(ConnectionCodeContext.Doc(connection.Name)).Append("' from the script's connections, created on first use.</summary>\n")
                .Append("    public static ").Append(context.ClassName).Append(' ').Append(codeName)
                .Append(" => ").Append(field).Append(" ??= new ").Append(context.ClassName).Append("();\n\n");
        }

        var source = new StringBuilder()
            .Append("/// <summary>The connections the script lists in its front matter (generated by RunPad).</summary>\n")
            .Append("public static partial class Connections\n{\n")
            .Append(properties)
            .Append(classes)
            .Append("}\n");
        return new GeneratedConnections(source.ToString(), packages, errors);
    }

    private static string Indent(string text, string indent) =>
        string.Join("\n", text.Split('\n').Select(line => line.Length == 0 ? line : indent + line));
}
