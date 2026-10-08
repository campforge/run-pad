using System.Linq;
using Xunit;

namespace RunPad.Runtime.Tests;

// TODO.md #111 (bucket 1) — LINQPad-inspired Util.Cache/CacheAsync. Same tradeoffs as
// UtilFileAndTryTests: writes into the REAL %AppData%/RunPad/UserData/Cache folder (no injectable
// path seam for a static-class API like this), every test uses its own Guid-named key so concurrent
// test runs can't collide, and cleans up after itself.
public class UtilCacheTests
{
    [Fact]
    public void Cache_Should_CallTheFunction_AndReturnItsResult_OnFirstCall()
    {
        var key = $"runpad-test-{Guid.NewGuid()}";
        try
        {
            var result = Util.Cache(() => 42, key);

            Assert.Equal(42, result);
        }
        finally { DeleteIfExists(key); }
    }

    [Fact]
    public void Cache_Should_ReturnTheCachedValue_WithoutCallingTheFunctionAgain_OnASecondCall()
    {
        var key = $"runpad-test-{Guid.NewGuid()}";
        try
        {
            var calls = 0;
            Func<int> function = () => { calls++; return calls; };

            var first = Util.Cache(function, key);
            var second = Util.Cache(function, key);

            Assert.Equal(1, first);
            Assert.Equal(1, second);
            Assert.Equal(1, calls);
        }
        finally { DeleteIfExists(key); }
    }

    [Fact]
    public void Cache_Should_NeverExpire_WhenDurationIsNull()
    {
        var key = $"runpad-test-{Guid.NewGuid()}";
        try
        {
            Util.Cache(() => "first", key);
            var second = Util.Cache(() => "second", key);

            Assert.Equal("first", second);
        }
        finally { DeleteIfExists(key); }
    }

    [Fact]
    public async Task Cache_Should_RecomputeAndOverwrite_AfterTheDurationExpires()
    {
        var key = $"runpad-test-{Guid.NewGuid()}";
        try
        {
            Util.Cache(() => "first", key, TimeSpan.FromMilliseconds(1));
            await Task.Delay(50);

            var second = Util.Cache(() => "second", key);

            Assert.Equal("second", second);
        }
        finally { DeleteIfExists(key); }
    }

    [Fact]
    public void Cache_Should_KeepDifferentKeys_Isolated()
    {
        var keyA = $"runpad-test-{Guid.NewGuid()}-a";
        var keyB = $"runpad-test-{Guid.NewGuid()}-b";
        try
        {
            var a = Util.Cache(() => "value-a", keyA);
            var b = Util.Cache(() => "value-b", keyB);

            Assert.Equal("value-a", a);
            Assert.Equal("value-b", b);
        }
        finally
        {
            DeleteIfExists(keyA);
            DeleteIfExists(keyB);
        }
    }

    [Fact]
    public void Cache_Should_Work_WhenTheKeyContainsPathAndOtherInvalidFileNameCharacters()
    {
        var key = $"runpad-test-{Guid.NewGuid()}/weird:key*name";
        try
        {
            var first = Util.Cache(() => "first", key);
            var second = Util.Cache(() => "second", key);

            Assert.Equal("first", first);
            Assert.Equal("first", second);
        }
        finally { DeleteIfExists(key); }
    }

    [Fact]
    public async Task CacheAsync_Should_CallTheFunction_AndReturnItsResult_OnFirstCall()
    {
        var key = $"runpad-test-{Guid.NewGuid()}";
        try
        {
            var result = await Util.CacheAsync(() => Task.FromResult(42), key);

            Assert.Equal(42, result);
        }
        finally { DeleteIfExists(key); }
    }

    [Fact]
    public async Task CacheAsync_Should_ReturnTheCachedValue_WithoutCallingTheFunctionAgain_OnASecondCall()
    {
        var key = $"runpad-test-{Guid.NewGuid()}";
        try
        {
            var calls = 0;
            Func<Task<int>> function = () => { calls++; return Task.FromResult(calls); };

            var first = await Util.CacheAsync(function, key);
            var second = await Util.CacheAsync(function, key);

            Assert.Equal(1, first);
            Assert.Equal(1, second);
            Assert.Equal(1, calls);
        }
        finally { DeleteIfExists(key); }
    }

    // Util.Cache reaches the script's own System.Text.Json by reflection (no STJ package in
    // RunPad.Runtime): a corrupt file must still surface as the serializer's JsonException and be
    // treated as a miss, not as a wrapped TargetInvocationException that escapes to the script.
    [Fact]
    public void Cache_Should_Recompute_WhenTheCacheFileIsCorrupt()
    {
        var key = $"runpad-test-{Guid.NewGuid()}";
        try
        {
            Util.Cache(() => 1, key);
            File.WriteAllText(CachePath(key), "{ not json");

            var result = Util.Cache(() => 2, key);

            Assert.Equal(2, result);
            Assert.Equal(2, Util.Cache(() => 3, key)); // rewritten with the fresh value
        }
        finally { DeleteIfExists(key); }
    }

    public sealed class Report
    {
        public string Title { get; set; } = "";
        public List<int> Values { get; set; } = [];
        public DateTimeOffset CreatedAt { get; set; }
    }

    [Fact]
    public void Cache_Should_RoundTripAComplexType()
    {
        var key = $"runpad-test-{Guid.NewGuid()}";
        try
        {
            var created = new DateTimeOffset(2026, 10, 7, 12, 30, 0, TimeSpan.FromHours(3));
            Util.Cache(() => new Report { Title = "Q3 \"final\"", Values = [1, 2, 3], CreatedAt = created }, key);

            var cached = Util.Cache(() => new Report { Title = "recomputed" }, key);

            Assert.Equal("Q3 \"final\"", cached.Title);
            Assert.Equal([1, 2, 3], cached.Values);
            Assert.Equal(created, cached.CreatedAt);
        }
        finally { DeleteIfExists(key); }
    }

    private static string CachePath(string key)
    {
        var sanitized = new string(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RunPad", "UserData", "Cache", sanitized + ".json");
    }

    private static void DeleteIfExists(string key)
    {
        var path = CachePath(key);
        if (File.Exists(path)) File.Delete(path);
    }
}
