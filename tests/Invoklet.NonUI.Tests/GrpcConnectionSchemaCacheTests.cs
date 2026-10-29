using Google.Protobuf.Reflection;
using Invoklet.Connectors.Grpc;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class GrpcConnectionSchemaCacheTests
{
    [Fact]
    public void ComputeCacheKey_SameAddressAndMetadata_ReturnsSameKey()
    {
        var address = "https://localhost:5001";
        var metadata = new Dictionary<string, string> { ["authorization"] = "Bearer token" };

        var key1 = GrpcConnectionSchemaCache.ComputeCacheKey(address, metadata);
        var key2 = GrpcConnectionSchemaCache.ComputeCacheKey(address, metadata);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void ComputeCacheKey_DifferentMetadata_ReturnsDifferentKey()
    {
        var address = "https://localhost:5001";
        var metadata1 = new Dictionary<string, string> { ["authorization"] = "Bearer token1" };
        var metadata2 = new Dictionary<string, string> { ["authorization"] = "Bearer token2" };

        var key1 = GrpcConnectionSchemaCache.ComputeCacheKey(address, metadata1);
        var key2 = GrpcConnectionSchemaCache.ComputeCacheKey(address, metadata2);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Cache_Stores_And_Retrieves_Descriptors()
    {
        var cache = new GrpcConnectionSchemaCache();
        var descriptors = new List<FileDescriptorProto>
        {
            new FileDescriptorProto { Name = "test.proto" }
        };
        var cacheKey = "test-key";

        cache.CacheDescriptors(cacheKey, descriptors);
        var result = cache.TryGetCachedDescriptors(cacheKey, out var retrieved);

        Assert.True(result);
        Assert.Equal(descriptors.Count, retrieved.Count);
        Assert.Equal("test.proto", retrieved[0].Name);
    }

    [Fact]
    public void Cache_Invalidate_RemovesCachedEntry()
    {
        var cache = new GrpcConnectionSchemaCache();
        var descriptors = new List<FileDescriptorProto>
        {
            new FileDescriptorProto { Name = "test.proto" }
        };
        var cacheKey = "test-key";

        cache.CacheDescriptors(cacheKey, descriptors);
        cache.InvalidateCache(cacheKey);
        var result = cache.TryGetCachedDescriptors(cacheKey, out _);

        Assert.False(result);
    }

    [Fact]
    public void ComputeDescriptorHash_SameDescriptors_ReturnsSameHash()
    {
        var cache = new GrpcConnectionSchemaCache();
        var descriptors = new List<FileDescriptorProto>
        {
            new FileDescriptorProto { Name = "test.proto" }
        };

        var hash1 = cache.ComputeDescriptorHash(descriptors);
        var hash2 = cache.ComputeDescriptorHash(descriptors);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeDescriptorHash_DifferentDescriptors_ReturnsDifferentHash()
    {
        var cache = new GrpcConnectionSchemaCache();
        var descriptors1 = new List<FileDescriptorProto>
        {
            new FileDescriptorProto { Name = "test1.proto" }
        };
        var descriptors2 = new List<FileDescriptorProto>
        {
            new FileDescriptorProto { Name = "test2.proto" }
        };

        var hash1 = cache.ComputeDescriptorHash(descriptors1);
        var hash2 = cache.ComputeDescriptorHash(descriptors2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void VerifyDescriptorHash_CorrectHash_ReturnsTrue()
    {
        var cache = new GrpcConnectionSchemaCache();
        var descriptors = new List<FileDescriptorProto>
        {
            new FileDescriptorProto { Name = "test.proto" }
        };

        var hash = cache.ComputeDescriptorHash(descriptors);
        var isValid = cache.VerifyDescriptorHash(descriptors, hash);

        Assert.True(isValid);
    }

    [Fact]
    public void VerifyDescriptorHash_IncorrectHash_ReturnsFalse()
    {
        var cache = new GrpcConnectionSchemaCache();
        var descriptors = new List<FileDescriptorProto>
        {
            new FileDescriptorProto { Name = "test.proto" }
        };

        var isValid = cache.VerifyDescriptorHash(descriptors, "wrong-hash");

        Assert.False(isValid);
    }




    [Fact]
    public async Task ConcurrentCacheAccess_ThreadSafe()
    {
        var cache = new GrpcConnectionSchemaCache();
        var descriptors = new List<FileDescriptorProto>
        {
            new FileDescriptorProto { Name = "test.proto" }
        };
        var cacheKey = "test-key";

        // Concurrent write and read
        var cancellationToken = TestContext.Current.CancellationToken;
        var writeTask = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
                cache.CacheDescriptors(cacheKey, descriptors);
        }, cancellationToken);

        var readTask = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
                cache.TryGetCachedDescriptors(cacheKey, out _);
        }, cancellationToken);

        await Task.WhenAll(writeTask, readTask);

        // Verify final state is valid
        var result = cache.TryGetCachedDescriptors(cacheKey, out var retrieved);
        Assert.True(result);
        Assert.Equal("test.proto", retrieved[0].Name);
    }
}
