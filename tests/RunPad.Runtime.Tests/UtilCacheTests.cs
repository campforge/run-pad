using System.Linq;
using Xunit;

namespace DotPad.Runtime.Tests;

// TODO.md #111 (bucket 1) — LINQPad-inspired Util.Cache/CacheAsync. Same tradeoffs as
// UtilFileAndTryTests: writes into the REAL %AppData%/DotPad/UserData/Cache folder (no injectable
// path seam for a static-class API like this), every test uses its own Guid-named key so concurrent
// test runs can't collide, and cleans up after itself.
public class UtilCacheTests
{
    [Fact]
    public void Cache_Should_CallTheFunction_AndReturnItsResult_OnFirstCall()
    {
        var key = $"dotpad-test-{Guid.NewGuid()}";
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
        var key = $"dotpad-test-{Guid.NewGuid()}";
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
        var key = $"dotpad-test-{Guid.NewGuid()}";
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
        var key = $"dotpad-test-{Guid.NewGuid()}";
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
        var keyA = $"dotpad-test-{Guid.NewGuid()}-a";
        var keyB = $"dotpad-test-{Guid.NewGuid()}-b";
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
        var key = $"dotpad-test-{Guid.NewGuid()}/weird:key*name";
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
        var key = $"dotpad-test-{Guid.NewGuid()}";
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
        var key = $"dotpad-test-{Guid.NewGuid()}";
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

    private static void DeleteIfExists(string key)
    {
        var sanitized = new string(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DotPad", "UserData", "Cache", sanitized + ".json");
        if (File.Exists(path)) File.Delete(path);
    }
}
