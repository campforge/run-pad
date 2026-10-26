using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace RunPad.Connectors.Grpc;

/// <summary>
/// Per-connection schema descriptor cache. Stores fetched FileDescriptorProto sets by cache key
/// (address + metadata hash), so a refresh of an unchanged connection can skip the server round
/// trip that GrpcReflection.FilesForAsync would otherwise repeat.
/// </summary>
internal sealed class GrpcConnectionSchemaCache
{
    // Cache entry: descriptors + integrity hash
    private sealed record CacheEntry(
        IReadOnlyList<FileDescriptorProto> Descriptors,
        string DescriptorHash);

    // Per cache key (address + metadata combination): the cached descriptor set and its hash
    private readonly Dictionary<string, CacheEntry> _descriptorCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Attempt to retrieve cached descriptors for the given cache key.
    /// Returns true and sets <paramref name="descriptors"/> if available.
    /// </summary>
    public bool TryGetCachedDescriptors(string cacheKey,
                                        out IReadOnlyList<FileDescriptorProto> descriptors)
    {
        descriptors = null!;
        lock (_descriptorCache)
        {
            if (!_descriptorCache.TryGetValue(cacheKey, out var entry))
                return false;
            descriptors = entry.Descriptors;
            return true;
        }
    }

    /// <summary>
    /// Store fetched descriptors in the cache for the given cache key.
    /// Computes and stores a SHA256 hash for integrity verification during rollback.
    /// </summary>
    public void CacheDescriptors(string cacheKey, IReadOnlyList<FileDescriptorProto> descriptors)
    {
        var hash = ComputeDescriptorHash(descriptors);
        lock (_descriptorCache)
        {
            _descriptorCache[cacheKey] = new CacheEntry(descriptors, hash);
        }
    }

    // Rollback-on-failure is already provided by the caller: ConnectionSchemaCache
    // (RunPad.Contracts/Connections/ConnectionSchema.cs) only replaces its stored schema on a
    // successful fetch and records an error otherwise, so the previous schema is never overwritten
    // by a failed refresh. No separate previous-schema tracking is needed here.

    /// <summary>
    /// Compute a SHA256 hash of the descriptor set, to check a cached set is still what was fetched.
    /// </summary>
    public string ComputeDescriptorHash(IReadOnlyList<FileDescriptorProto> descriptors)
    {
        using var hasher = SHA256.Create();
        // A descriptor list is not itself a protobuf message. Wrap it in the same
        // FileDescriptorSet envelope used by the reflection protocol before hashing.
        var set = new FileDescriptorSet();
        set.File.Add(descriptors);
        var buffer = set.ToByteArray();
        var hash = hasher.ComputeHash(buffer);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Verify that a descriptor set matches a previously computed <see cref="ComputeDescriptorHash"/>.
    /// </summary>
    public bool VerifyDescriptorHash(IReadOnlyList<FileDescriptorProto> descriptors, string expectedHash)
        => ComputeDescriptorHash(descriptors).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Clear the cache entry for a given key (e.g., when connection is deleted).
    /// </summary>
    public void InvalidateCache(string cacheKey)
    {
        lock (_descriptorCache)
        {
            _descriptorCache.Remove(cacheKey);
        }
    }

    /// <summary>
    /// Compute a stable cache key from connection parameters (address + metadata).
    /// Two connections are considered the same for caching purposes if both their
    /// address and metadata (headers) are identical.
    /// </summary>
    public static string ComputeCacheKey(string address, IReadOnlyDictionary<string, string>? metadata)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(address);

        // Include metadata to differentiate connections with different auth/headers
        if (metadata is not null)
        {
            foreach (var (key, value) in metadata.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.Append('|').Append(key).Append('=').Append(value);
            }
        }

        // Hash to a fixed-size key
        using var hasher = SHA256.Create();
        var hash = hasher.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }
}
