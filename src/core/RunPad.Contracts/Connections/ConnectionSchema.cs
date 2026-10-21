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
public sealed record ConnectionTools
{
    /// <summary>The folder of a NuGet package (id, version), downloaded first when it isn't there yet.</summary>
    public required Func<string, string, CancellationToken, Task<string>> GetPackageFolderAsync { get; init; }

    /// <summary>The folder of the script the schema is read for, which relative file paths in the
    /// parameters are read from; null for a script never saved, or outside a script.</summary>
    public string? ScriptDirectory { get; init; }
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

    /// <summary>
    /// Whether the schema depends on the script's folder, as a database file given by a relative
    /// path does: such a connection's schema is kept for each folder it is used from.
    /// </summary>
    bool UsesScriptDirectory(RpadConnection connection) => false;

    /// <summary>Change when cached descriptions need to be read again after a driver update.</summary>
    string SchemaVersion => "";
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
/// placeholders included, so no secret value is part of the key) and, for a connection whose driver
/// says it depends on it, the script's folder; and the last reason one couldn't be read. Shared by
/// the window, the editor and the command-line runner.
/// </summary>
/// <param name="drivers">Tells which connections depend on the script's folder; none do without it.</param>
public sealed class ConnectionSchemaCache(string folder, ConnectionDriverRegistry? drivers = null)
{
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    // The reads under way, by key: the editor, a run and the Connections window often ask for the
    // same schema at once, and each read may be a server round trip plus a code generator.
    private readonly Dictionary<string, Task<bool>> _fetches = new(StringComparer.Ordinal);
    // Generated contracts can be large. Keep a bounded hot set, invalidated by file metadata,
    // instead of reparsing the same JSON during every editor/context refresh.
    private readonly Dictionary<string, CachedDescription> _descriptions = new(StringComparer.Ordinal);
    private sealed record CachedDescription(long Written, long Created, long Length, ConnectionSchema Schema);
    private readonly object _lock = new();

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RunPad", "connection-schemas");

    public string Folder { get; } = folder;

    public string KeyOf(RpadConnection connection, string? scriptDirectory = null)
    {
        var folderPart = drivers?.Find(connection.Driver) is IConnectionSchemaDriver driver && driver.UsesScriptDirectory(connection)
            ? "\n" + (scriptDirectory is null ? "" : Path.GetFullPath(scriptDirectory))
            : "";
        var versionPart = drivers?.Find(connection.Driver) is IConnectionSchemaDriver { SchemaVersion.Length: > 0 } versioned
            ? "\nversion:" + versioned.SchemaVersion : "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            connection.Driver.ToLowerInvariant() + "\n" + JsonSerializer.Serialize(connection.Params) + folderPart + versionPart))).ToLowerInvariant()[..32];
    }

    /// <summary>The cached schema, or null when there is none (or it can't be read).</summary>
    public ConnectionSchema? Get(RpadConnection connection, string? scriptDirectory = null)
    {
        var key = KeyOf(connection, scriptDirectory);
        var path = Path.Combine(Folder, key + ".json");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                lock (_lock) _descriptions.Remove(key);
                return null;
            }
            var written = info.LastWriteTimeUtc.Ticks;
            var created = info.CreationTimeUtc.Ticks;
            var length = info.Length;
            lock (_lock)
                if (_descriptions.TryGetValue(key, out var cached)
                    && cached.Written == written && cached.Created == created && cached.Length == length)
                    return cached.Schema;

            // File access and JSON parsing happen outside the cache lock. The editor can keep using
            // an already loaded schema while another caller refreshes a cold or changed entry.
            var schema = JsonSerializer.Deserialize<ConnectionSchema>(File.ReadAllText(path));
            if (schema is null)
            {
                lock (_lock) _descriptions.Remove(key);
                return null;
            }
            lock (_lock)
            {
                if (_descriptions.TryGetValue(key, out var cached)
                    && cached.Written == written && cached.Created == created && cached.Length == length)
                    return cached.Schema;
                if (_descriptions.Count >= 16) _descriptions.Remove(_descriptions.Keys.First());
                _descriptions[key] = new CachedDescription(written, created, length, schema);
            }
            return schema;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            lock (_lock) _descriptions.Remove(key);
            return null;
        }
    }

    /// <summary>Returns a schema already loaded in this process, without checking or reading disk.</summary>
    public ConnectionSchema? TryGetLoaded(RpadConnection connection, string? scriptDirectory = null)
    {
        var key = KeyOf(connection, scriptDirectory);
        lock (_lock) return _descriptions.TryGetValue(key, out var cached) ? cached.Schema : null;
    }

    /// <summary>Why the schema couldn't be read the last time this RunPad tried, or null.</summary>
    public string? ErrorFor(RpadConnection connection, string? scriptDirectory = null)
    {
        lock (_lock) return _errors.GetValueOrDefault(KeyOf(connection, scriptDirectory));
    }

    public void Put(RpadConnection connection, ConnectionSchema schema, string? scriptDirectory = null)
    {
        Directory.CreateDirectory(Folder);
        var key = KeyOf(connection, scriptDirectory);
        var target = Path.Combine(Folder, key + ".json");
        // Named after the key, so one left over (the process killed mid-write) tells whose it was.
        var temp = Path.Combine(Folder, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(schema));
            try
            {
                File.Move(temp, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // On Windows another RunPad (the window and the CLI share the folder) may be reading
                // the file for a moment.
                Thread.Sleep(100);
                File.Move(temp, target, overwrite: true);
            }
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        lock (_lock)
        {
            _errors.Remove(key);
            _descriptions.Remove(key);
        }
        // Fetches publish on a worker thread: warm the description there before the UI is notified.
        _ = Get(connection, scriptDirectory);
    }

    /// <summary>
    /// Reads and caches the schema of the connections that need one, as <paramref name="refresh"/>
    /// says which. Returns true when any schema or error changed. Failures are kept for
    /// <see cref="ErrorFor"/>; a missing secret or variable is such a failure too. A schema already
    /// being read for another caller isn't read a second time: this call waits for that read.
    /// Cancelling stops the wait, not the read, which still records its schema or error.
    /// </summary>
    public async Task<bool> EnsureAsync(IEnumerable<RpadConnection> connections, string? scriptDirectory,
        ConnectionDriverRegistry drivers, Func<string, string?> secret, ConnectionTools tools, ConnectionSchemaRefresh refresh,
        CancellationToken cancellationToken)
    {
        var changed = false;
        tools = tools with { ScriptDirectory = scriptDirectory };
        foreach (var connection in connections)
        {
            if (drivers.Find(connection.Driver) is not IConnectionSchemaDriver driver || driver.Validate(connection).Count > 0) continue;
            if (refresh != ConnectionSchemaRefresh.All && Get(connection, scriptDirectory) is not null) continue;
            if (refresh == ConnectionSchemaRefresh.Missing && ErrorFor(connection, scriptDirectory) is not null) continue;
            var key = KeyOf(connection, scriptDirectory);
            var resolved = ConnectionPlaceholderResolver.Resolve(connection.Params, secret, Environment.GetEnvironmentVariable);
            if (resolved.Errors.Count > 0)
            {
                lock (_lock) _errors[key] = string.Join(" ", resolved.Errors);
                changed = true;
                continue;
            }
            if (await FetchOnceAsync(key, connection, driver, resolved.Params, tools, scriptDirectory, refresh)
                .WaitAsync(cancellationToken).ConfigureAwait(false))
                changed = true;
        }
        return changed;
    }

    // The read under way for this key, or a new one. It runs on the thread pool, as some drivers read
    // synchronously, and without any caller's token: it is shared, so one caller giving up must not
    // cancel it for the others, nor record the cancellation as the schema's error. False when there
    // was nothing to read after all: a read that finished just before this call came for it.
    private Task<bool> FetchOnceAsync(string key, RpadConnection connection, IConnectionSchemaDriver driver,
        IReadOnlyDictionary<string, RpadValue> resolvedParams, ConnectionTools tools, string? scriptDirectory,
        ConnectionSchemaRefresh refresh)
    {
        var cached = refresh != ConnectionSchemaRefresh.All ? Get(connection, scriptDirectory) : null;
        Task<bool> fetch;
        lock (_lock)
        {
            if (_fetches.TryGetValue(key, out var running)) return running;
            if (refresh != ConnectionSchemaRefresh.All && (cached is not null || _descriptions.ContainsKey(key)))
                return Task.FromResult(false);
            if (refresh == ConnectionSchemaRefresh.Missing && _errors.ContainsKey(key)) return Task.FromResult(false);
            fetch = Task.Run(async () =>
            {
                try
                {
                    Put(connection, await driver.FetchSchemaAsync(resolvedParams, tools, CancellationToken.None).ConfigureAwait(false),
                        scriptDirectory);
                }
                catch (Exception ex)
                {
                    lock (_lock) _errors[key] = ex.Message;
                }
                return true;
            });
            _fetches[key] = fetch;
        }
        // Registered after the task is listed, so a read that is already over still leaves the list.
        _ = fetch.ContinueWith(_ =>
        {
            lock (_lock)
            {
                if (_fetches.TryGetValue(key, out var listed) && listed == fetch) _fetches.Remove(key);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return fetch;
    }
}

/// <summary>
/// Everything RunPad needs to read connection schemas: the drivers, the cache, the tools and where
/// secrets come from (the Secret Store in the app, the same file through the runtime in the CLI).
/// </summary>
public sealed class ConnectionSchemas(ConnectionDriverRegistry drivers, string cacheFolder, ConnectionTools tools,
    Func<string, string?> secret)
{
    public ConnectionDriverRegistry Drivers { get; } = drivers;

    public ConnectionSchemaCache Cache { get; } = new(cacheFolder, drivers);

    /// <param name="scriptDirectory">The script's folder, for relative file paths; null outside a saved script.</param>
    public Task<bool> EnsureAsync(IEnumerable<RpadConnection> connections, string? scriptDirectory, ConnectionSchemaRefresh refresh,
        CancellationToken cancellationToken) =>
        Cache.EnsureAsync(connections, scriptDirectory, Drivers, secret, tools, refresh, cancellationToken);
}
