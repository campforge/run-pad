using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RunPad.Format.RPad;

namespace RunPad.Contracts.Connections;

/// <summary>A C# file a connection adds to the script's build and editor, beside the Connections class.</summary>
public sealed record GeneratedSourceFile(string Name, string Text);

/// <summary>
/// What a driver learned from the endpoint itself, such as a gRPC server's services: the C# that
/// describes them (message and client classes) and the driver's own notes for writing the client.
/// </summary>
/// <param name="Summary">A line for the user, such as "2 services: inventory.v1.Stock, …".</param>
/// <param name="Files">C# files compiled with the script.</param>
/// <param name="Data">The driver's own data for <see cref="IConnectionDriver.CreateClient"/>.</param>
public sealed record ConnectionSchema(string Summary, IReadOnlyList<GeneratedSourceFile> Files, string Data);

/// <summary>What a driver may use while reading a schema: NuGet packages with build tools, for instance.</summary>
public sealed class ConnectionTools
{
    /// <summary>The folder of a NuGet package (id, version), downloaded first when it isn't there yet.</summary>
    public required Func<string, string, CancellationToken, Task<string>> GetPackageFolderAsync { get; init; }
}

/// <summary>
/// A driver whose client depends on the endpoint's own description, read by RunPad (not by the
/// script) before the script is built: a gRPC server's services through server reflection.
/// </summary>
public interface IConnectionSchemaDriver : IConnectionDriver
{
    /// <summary>Reads the endpoint's description with placeholders already replaced. Throws with a
    /// message for the user when the endpoint can't be reached or described.</summary>
    Task<ConnectionSchema> FetchSchemaAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, ConnectionTools tools,
        CancellationToken cancellationToken);
}

/// <summary>Which schemas <see cref="ConnectionSchemaCache.EnsureAsync"/> reads.</summary>
public enum ConnectionSchemaRefresh
{
    /// <summary>Those never read and never failed: what the editor does in the background, so an
    /// unreachable server isn't asked again on every edit.</summary>
    Missing,

    /// <summary>Those not cached, failed ones included: before a run.</summary>
    Failed,

    /// <summary>All of them again: the Refresh and Test buttons.</summary>
    All,
}

/// <summary>
/// The schemas RunPad has read, kept on disk by the connection's driver and parameters (as written,
/// placeholders included, so no secret value is part of the key), and the last reason one couldn't
/// be read. Shared by the window, the editor and the command-line runner.
/// </summary>
public sealed class ConnectionSchemaCache(string folder)
{
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RunPad", "connection-schemas");

    public string Folder { get; } = folder;

    public static string KeyOf(RpadConnection connection) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        connection.Driver.ToLowerInvariant() + "\n" + JsonSerializer.Serialize(connection.Params)))).ToLowerInvariant()[..32];

    /// <summary>The cached schema, or null when there is none (or it can't be read).</summary>
    public ConnectionSchema? Get(RpadConnection connection)
    {
        var path = Path.Combine(Folder, KeyOf(connection) + ".json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ConnectionSchema>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Why the schema couldn't be read the last time this RunPad tried, or null.</summary>
    public string? ErrorFor(RpadConnection connection)
    {
        lock (_lock) return _errors.GetValueOrDefault(KeyOf(connection));
    }

    public void Put(RpadConnection connection, ConnectionSchema schema)
    {
        Directory.CreateDirectory(Folder);
        var key = KeyOf(connection);
        var temp = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(schema));
        File.Move(temp, Path.Combine(Folder, key + ".json"), overwrite: true);
        lock (_lock) _errors.Remove(key);
    }

    /// <summary>
    /// Reads and caches the schema of the connections that need one, as <paramref name="refresh"/>
    /// says which. Returns true when any schema or error changed. Failures are kept for
    /// <see cref="ErrorFor"/>; a missing secret or variable is such a failure too.
    /// </summary>
    public async Task<bool> EnsureAsync(IEnumerable<RpadConnection> connections, ConnectionDriverRegistry drivers,
        Func<string, string?> secret, ConnectionTools tools, ConnectionSchemaRefresh refresh, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var connection in connections)
        {
            if (drivers.Find(connection.Driver) is not IConnectionSchemaDriver driver || driver.Validate(connection).Count > 0) continue;
            if (refresh != ConnectionSchemaRefresh.All && Get(connection) is not null) continue;
            if (refresh == ConnectionSchemaRefresh.Missing && ErrorFor(connection) is not null) continue;
            var key = KeyOf(connection);
            var resolved = ConnectionPlaceholderResolver.Resolve(connection.Params, secret, Environment.GetEnvironmentVariable);
            if (resolved.Errors.Count > 0)
            {
                lock (_lock) _errors[key] = string.Join(" ", resolved.Errors);
                changed = true;
                continue;
            }
            try
            {
                Put(connection, await driver.FetchSchemaAsync(resolved.Params, tools, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                lock (_lock) _errors[key] = ex.Message;
            }
            changed = true;
        }
        return changed;
    }
}

/// <summary>
/// Everything RunPad needs to read connection schemas: the drivers, the cache, the tools and where
/// secrets come from (the Secret Store in the app, the same file through the runtime in the CLI).
/// </summary>
public sealed class ConnectionSchemas(ConnectionDriverRegistry drivers, ConnectionSchemaCache cache, ConnectionTools tools,
    Func<string, string?> secret)
{
    public ConnectionDriverRegistry Drivers { get; } = drivers;

    public ConnectionSchemaCache Cache { get; } = cache;

    public Task<bool> EnsureAsync(IEnumerable<RpadConnection> connections, ConnectionSchemaRefresh refresh,
        CancellationToken cancellationToken) =>
        Cache.EnsureAsync(connections, Drivers, secret, tools, refresh, cancellationToken);
}
