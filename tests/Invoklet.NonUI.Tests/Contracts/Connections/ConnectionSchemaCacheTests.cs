using Invoklet.Contracts.Connections;
using Invoklet.Format.MdRun;
using Xunit;

namespace Invoklet.NonUI.Tests.Contracts.Connections;

// The schema cache asks the endpoint once for what several callers want at the same time: the
// editor, a run and the Connections window often do.
public sealed class ConnectionSchemaCacheTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("Invoklet-schema-cache").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    // Holds every read until released, and counts them.
    private sealed class SlowDriver : IConnectionSchemaDriver
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Fetches;

        public string Id => "slow";
        public string DisplayName => "Slow";
        public ConnectionCategory Category => ConnectionCategory.Grpc;
        public IReadOnlyList<ConnectionParam> Params => [];
        public IReadOnlyDictionary<string, string> Packages => new Dictionary<string, string>();
        public IReadOnlyList<string> Validate(MdRunConnection connection) => [];

        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(true, ""));

        public ConnectionClientCode CreateClient(MdRunConnection connection, ConnectionCodeContext context) => new("");

        public async Task<ConnectionSchema> FetchSchemaAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, ConnectionTools tools,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Fetches);
            await Release.Task.WaitAsync(cancellationToken);
            return new ConnectionSchema("1 service", [], "");
        }
    }

    private static readonly MdRunConnection Connection = new()
    {
        Name = "stock",
        Driver = "slow",
        Params = new(StringComparer.Ordinal) { ["address"] = MdRunValue.Of("http://localhost:5000") },
    };

    private static readonly ConnectionTools Tools = new()
    {
        GetPackageFolderAsync = (_, _, _) => Task.FromResult(""),
    };

    private (ConnectionSchemas Schemas, SlowDriver Driver) Create()
    {
        var driver = new SlowDriver();
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(driver);
        return (new ConnectionSchemas(drivers, Path.Combine(_folder, "schemas"), Tools, _ => null), driver);
    }

    [Fact]
    public void WarmDescriptions_Should_BeReused_AndExternalChangesInvalidated()
    {
        var (schemas, _) = Create();
        var cache = schemas.Cache;
        cache.Put(Connection, new ConnectionSchema("first", [new("Large.cs", new string(' ', 2_000_000))], ""));
        var first = cache.Get(Connection)!;
        Assert.Same(first, cache.Get(Connection));
        var external = new ConnectionSchemaCache(cache.Folder);
        external.Put(Connection, new ConnectionSchema("replaced externally", [], ""));
        var updated = cache.Get(Connection)!;
        Assert.NotSame(first, updated);
        Assert.Equal("replaced externally", updated.Summary);
        Assert.Same(updated, cache.Get(Connection));
        cache.Put(Connection, new ConnectionSchema("local replacement", [], ""));
        Assert.Equal("local replacement", cache.Get(Connection)!.Summary);
        File.Delete(Path.Combine(cache.Folder, cache.KeyOf(Connection) + ".json"));
        Assert.Null(cache.Get(Connection));
    }

    [Fact]
    public void TryGetLoaded_Should_NotReadPersistedSchemaUntilGetIsCalled()
    {
        var (schemas, _) = Create();
        var cache = schemas.Cache;
        var schema = new ConnectionSchema("persisted", [], "");
        Directory.CreateDirectory(cache.Folder);
        File.WriteAllText(Path.Combine(cache.Folder, cache.KeyOf(Connection) + ".json"),
            System.Text.Json.JsonSerializer.Serialize(schema));

        Assert.Null(cache.TryGetLoaded(Connection));
        Assert.Equal(schema.Summary, cache.Get(Connection)!.Summary);
        Assert.Equal("persisted", cache.TryGetLoaded(Connection)!.Summary);
    }

    [Fact]
    public async Task ConcurrentCallers_Should_ShareOneRead()
    {
        var (schemas, driver) = Create();
        var ct = TestContext.Current.CancellationToken;
        var first = schemas.EnsureAsync([Connection], null, ConnectionSchemaRefresh.Missing, ct);
        var second = schemas.EnsureAsync([Connection], null, ConnectionSchemaRefresh.Failed, ct);
        // The Test button joins the read under way too.
        var third = schemas.EnsureAsync([Connection], null, ConnectionSchemaRefresh.All, ct);
        driver.Release.SetResult();

        Assert.All(await Task.WhenAll(first, second, third), Assert.True);
        Assert.Equal(1, driver.Fetches);
        Assert.Equal("1 service", schemas.Cache.Get(Connection)!.Summary);
        Assert.False(await schemas.EnsureAsync([Connection], null, ConnectionSchemaRefresh.Missing, ct));
        Assert.Equal(1, driver.Fetches);
        // Once it is over, Refresh reads again.
        Assert.True(await schemas.EnsureAsync([Connection], null, ConnectionSchemaRefresh.All, ct));
        Assert.Equal(2, driver.Fetches);
    }

    // A caller that gives up (a cancelled run) stops waiting; the others still get the schema, and no
    // cancellation is recorded as its error.
    [Fact]
    public async Task ACancelledCaller_Should_LeaveTheSharedRead_ToTheOthers()
    {
        var (schemas, driver) = Create();
        using var cancel = new CancellationTokenSource();
        var cancelled = schemas.EnsureAsync([Connection], null, ConnectionSchemaRefresh.Failed, cancel.Token);
        var other = schemas.EnsureAsync([Connection], null, ConnectionSchemaRefresh.Missing, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Null(schemas.Cache.ErrorFor(Connection));

        driver.Release.SetResult();
        Assert.True(await other);
        Assert.Equal(1, driver.Fetches);
        Assert.NotNull(schemas.Cache.Get(Connection));
        Assert.Null(schemas.Cache.ErrorFor(Connection));
    }

    // The temporary file a write goes through doesn't stay behind, whether the write succeeds or not.
    [Fact]
    public void Put_Should_LeaveNoTemporaryFile()
    {
        var (schemas, _) = Create();
        var cache = schemas.Cache;
        cache.Put(Connection, new ConnectionSchema("1 service", [], ""));
        Assert.Equal([cache.KeyOf(Connection) + ".json"], Directory.GetFiles(cache.Folder).Select(Path.GetFileName));

        // A folder where the file should go: the move fails, twice (an IOException on Unix, access
        // denied on Windows).
        File.Delete(Path.Combine(cache.Folder, cache.KeyOf(Connection) + ".json"));
        Directory.CreateDirectory(Path.Combine(cache.Folder, cache.KeyOf(Connection) + ".json"));
        var failure = Assert.ThrowsAny<Exception>(() => cache.Put(Connection, new ConnectionSchema("1 service", [], "")));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure.ToString());
        Assert.Empty(Directory.GetFiles(cache.Folder));
    }

    // A connection removed, renamed or changed since leaves its old key's file behind forever
    // otherwise: PruneStale is what reclaims it, by age, since nothing else ever revisits that key.
    [Fact]
    public void PruneStale_Should_DeleteOnlyFilesOlderThanMaxAge()
    {
        var (schemas, _) = Create();
        var cache = schemas.Cache;
        var other = new MdRunConnection
        {
            Name = "other",
            Driver = "slow",
            Params = new(StringComparer.Ordinal) { ["address"] = MdRunValue.Of("http://localhost:5001") },
        };
        cache.Put(Connection, new ConnectionSchema("stale", [], ""));
        cache.Put(other, new ConnectionSchema("fresh", [], ""));
        File.SetLastWriteTimeUtc(Path.Combine(cache.Folder, cache.KeyOf(Connection) + ".json"), DateTime.UtcNow - TimeSpan.FromDays(10));

        cache.PruneStale(TimeSpan.FromDays(5));

        Assert.Null(cache.Get(Connection));
        Assert.Equal("fresh", cache.Get(other)!.Summary);
        Assert.Equal([cache.KeyOf(other) + ".json"], Directory.GetFiles(cache.Folder).Select(Path.GetFileName));
    }

    // A temp file left by a write interrupted mid-rename (the process killed) is never read by
    // anything, so it goes regardless of age.
    [Fact]
    public void PruneStale_Should_DeleteLeftoverTempFiles_RegardlessOfAge()
    {
        var (schemas, _) = Create();
        var cache = schemas.Cache;
        Directory.CreateDirectory(cache.Folder);
        var temp = Path.Combine(cache.Folder, cache.KeyOf(Connection) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temp, "{}");

        cache.PruneStale(TimeSpan.FromDays(365));

        Assert.Empty(Directory.GetFiles(cache.Folder));
    }

    // An editor showing a connection's classes learns of a read it didn't start (another window's
    // Refresh) from Changed: raised with the key for a schema written and for a read that failed.
    [Fact]
    public async Task Changed_Should_NameTheKey_OfASchemaWritten_OrAReadThatFailed()
    {
        var (schemas, driver) = Create();
        var keys = new List<string>();
        schemas.Cache.Changed += key => { lock (keys) keys.Add(key); };
        driver.Release.SetResult();

        Assert.True(await schemas.EnsureAsync([Connection], null, ConnectionSchemaRefresh.All, TestContext.Current.CancellationToken));
        Assert.Equal([schemas.Cache.KeyOf(Connection)], keys);

        var withSecret = new MdRunConnection
        {
            Name = "secret",
            Driver = "slow",
            Params = new(StringComparer.Ordinal) { ["address"] = MdRunValue.Of("http://${secret:missing}:5000") },
        };
        Assert.True(await schemas.EnsureAsync([withSecret], null, ConnectionSchemaRefresh.All, TestContext.Current.CancellationToken));
        Assert.Equal([schemas.Cache.KeyOf(Connection), schemas.Cache.KeyOf(withSecret)], keys);
        Assert.NotNull(schemas.Cache.ErrorFor(withSecret));
    }

    [Fact]
    public void PruneStale_Should_DoNothing_WhenTheFolderWasNeverCreated()
    {
        var cache = new ConnectionSchemaCache(Path.Combine(_folder, "never-created"));
        cache.PruneStale(TimeSpan.FromDays(1)); // must not throw
    }
}
