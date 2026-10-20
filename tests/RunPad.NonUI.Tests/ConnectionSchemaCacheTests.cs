using RunPad.Contracts.Connections;
using RunPad.Format.RPad;
using Xunit;

namespace RunPad.NonUI.Tests;

// The schema cache asks the endpoint once for what several callers want at the same time: the
// editor, a run and the Connections window often do.
public sealed class ConnectionSchemaCacheTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("runpad-schema-cache").FullName;

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
        public IReadOnlyList<string> Validate(RpadConnection connection) => [];

        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(true, ""));

        public ConnectionClientCode CreateClient(RpadConnection connection, ConnectionCodeContext context) => new("");

        public async Task<ConnectionSchema> FetchSchemaAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, ConnectionTools tools,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Fetches);
            await Release.Task.WaitAsync(cancellationToken);
            return new ConnectionSchema("1 service", [], "");
        }
    }

    private static readonly RpadConnection Connection = new()
    {
        Name = "stock",
        Driver = "slow",
        Params = new(StringComparer.Ordinal) { ["address"] = RpadValue.Of("http://localhost:5000") },
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
}
