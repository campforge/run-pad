using System.Text.RegularExpressions;
using Invoklet.Contracts.Connections;

namespace Invoklet.Connectors.Grpc;

/// <summary>
/// Finds the client class of each service in what protoc's gRPC plugin wrote: a
/// <c>static partial class Stock</c> in the file's namespace whose <c>__ServiceName</c> is the
/// service's full name, with a nested <c>StockClient</c>.
/// </summary>
internal static partial class GrpcClientParser
{
    [GeneratedRegex(@"^namespace\s+([\w.]+)")]
    private static partial Regex NamespaceLine();

    [GeneratedRegex(@"^\s*(?:public|internal)\s+static\s+partial\s+class\s+(\w+)")]
    private static partial Regex ServiceClassLine();

    [GeneratedRegex(@"__ServiceName\s*=\s*""([^""]+)""")]
    private static partial Regex ServiceNameLine();

    public static List<GrpcServiceClient> Find(IEnumerable<GeneratedSourceFile> files, IReadOnlyCollection<string> services)
    {
        var found = new List<GrpcServiceClient>();
        foreach (var file in files)
        {
            string? ns = null, serviceClass = null;
            foreach (var line in file.Text.Split('\n'))
            {
                if (NamespaceLine().Match(line) is { Success: true } n) ns = n.Groups[1].Value;
                else if (ServiceClassLine().Match(line) is { Success: true } c) serviceClass = c.Groups[1].Value;
                else if (ServiceNameLine().Match(line) is { Success: true } s && serviceClass is not null && services.Contains(s.Groups[1].Value))
                    found.Add(new GrpcServiceClient(s.Groups[1].Value,
                        "global::" + (ns is null ? "" : ns + ".") + serviceClass + "." + serviceClass + "Client", ""));
            }
        }
        return found;
    }

    // A property per service, named after it (Stock for inventory.v1.Stock); services of the same
    // name in different packages are told apart by their package (InventoryV1Stock).
    public static List<GrpcServiceClient> WithProperties(IReadOnlyList<GrpcServiceClient> clients, string? className = null)
    {
        var shortNames = clients.GroupBy(c => Pascal(c.Service.Split('.')[^1])).ToDictionary(g => g.Key, g => g.Count());
        var taken = new HashSet<string>(["Channel", "CallInvoker"], StringComparer.Ordinal);
        if (className is not null) taken.Add(className);
        var result = new List<GrpcServiceClient>();
        foreach (var client in clients)
        {
            var name = Pascal(client.Service.Split('.')[^1]);
            if (shortNames[name] > 1 || taken.Contains(name)) name = Pascal(client.Service);
            var unique = name;
            for (var i = 2; !taken.Add(unique); i++) unique = name + i;
            result.Add(client with { Property = unique });
        }
        return result;
    }

    private static string Pascal(string dotted) => string.Concat(dotted.Split('.', '_').Where(p => p.Length > 0)
        .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
}
