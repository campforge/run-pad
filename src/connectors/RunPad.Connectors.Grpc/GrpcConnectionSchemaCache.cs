using System.Security.Cryptography;
using Google.Protobuf.Reflection;

namespace RunPad.Connectors.Grpc;

/// <summary>
/// Per-connection schema descriptor cache with versioning to prevent stale work from overwriting
/// newer state. Stores FileDescriptorProto sets by cache key (address + metadata hash), along with
/// a monotonic version token. When a fetch completes, its version is checked: if a newer version
/// has been requested in the meantime, the stale result is discarded.
/// </summary>
internal sealed class GrpcConnectionSchemaCache
{
    // Cache entry: descriptors + version + integrity hash
    private sealed record CacheEntry(
        IReadOnlyList<FileDescriptorProto> Descriptors,
        long Version,
        string DescriptorHash);

    // Per cache key (address + metadata combination): the cached descriptor set and its version
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    
    // Global version counter: incremented for each refresh request
    private long _nextVersion = 1;

    /// <summary>Get the current version for a cache key. Returns 0 if not cached.</summary>
    public long GetCachedVersion(string cacheKey)
    {
        lock (_cache)
        {
            return _cache.TryGetValue(cacheKey, out var entry) ? entry.Version : 0;
        }
    }

    /// <summary>
    /// Attempt to retrieve cached descriptors if they match the requested version.
    /// Returns true and sets <paramref name="descriptors"/> if the cache hit the version.
    /// Returns false if cache is empty, or if a newer version has been requested.
    /// </summary>
    public bool TryGetCached(string cacheKey, long requestVersion, 
                             out IReadOnlyList<FileDescriptorProto> descriptors)
    {
        descriptors = null!;
        lock (_cache)
        {
            if (!_cache.TryGetValue(cacheKey, out var entry))
                return false;
            
            // Only return cached descriptors if version matches the request
            // (i.e., no newer refresh has been requested since this one started)
            if (entry.Version != requestVersion)
                return false;
            
            descriptors = entry.Descriptors;
            return true;
        }
    }

    /// <summary>
    /// Store fetched descriptors in the cache for the given cache key and version.
    /// Computes and stores a SHA256 hash for integrity verification during rollback.
    /// </summary>
    public void CacheDescriptors(string cacheKey, IReadOnlyList<FileDescriptorProto> descriptors, long version)
    {
        var hash = ComputeDescriptorHash(descriptors);
        lock (_cache)
        {
            _cache[cacheKey] = new CacheEntry(descriptors, version, hash);
        }
    }

    /// <summary>
    /// Get the next version number to tag a new fetch request with.
    /// Increments the global version counter atomically.
    /// </summary>
    public long GetNextVersion() => Interlocked.Increment(ref _nextVersion);

    /// <summary>
    /// Compute a SHA256 hash of the descriptor set for integrity verification.
    /// Used to confirm that a cached descriptor is still valid before restoring it on error.
    /// </summary>
    public string ComputeDescriptorHash(IReadOnlyList<FileDescriptorProto> descriptors)
    {
        using var hasher = SHA256.Create();
        var buffer = descriptors.ToByteArray();
        var hash = hasher.ComputeHash(buffer);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Verify that a descriptor set matches its stored hash.
    /// Used during rollback to confirm the previous schema is still valid.
    /// </summary>
    public bool VerifyDescriptorHash(IReadOnlyList<FileDescriptorProto> descriptors, string expectedHash)
        => ComputeDescriptorHash(descriptors).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Clear the cache entry for a given key (e.g., when connection is deleted).
    /// </summary>
    public void InvalidateCache(string cacheKey)
    {
        lock (_cache)
        {
            _cache.Remove(cacheKey);
        }
    }

    /// <summary>
    /// Compute a stable cache key from connection parameters (address + metadata).
    /// Two connections are considered the same for caching purposes if both their
    /// address and metadata (headers) are identical.
    /// </summary>
    public static string ComputeCacheKey(string address, IReadOnlyDictionary<string, string> metadata)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(address);
        
        // Include metadata to differentiate connections with different auth/headers
        foreach (var (key, value) in metadata.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append('|').Append(key).Append('=').Append(value);
        }
        
        // Hash to a fixed-size key
        using var hasher = SHA256.Create();
        var hash = hasher.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }
}
