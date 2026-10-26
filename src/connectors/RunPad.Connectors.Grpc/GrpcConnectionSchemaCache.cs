using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf.Reflection;
using RunPad.Contracts.Connections;

namespace RunPad.Connectors.Grpc;

/// <summary>
/// Per-connection schema descriptor cache with atomic refresh and rollback capability.
/// Stores fetched FileDescriptorProto sets by cache key (address + metadata hash) and provides
/// rollback to a previous schema on fetch failure.
/// 
/// Versioning is managed by the caller (e.g., ScriptConnectionsViewModel) to prevent race
/// conditions: the ViewModel tags each fetch request with a version, and ignores results
/// if a newer version has been requested in the meantime.
/// </summary>
internal sealed class GrpcConnectionSchemaCache
{
    // Cache entry: descriptors + integrity hash
    private sealed record CacheEntry(
        IReadOnlyList<FileDescriptorProto> Descriptors,
        string DescriptorHash);

    // Per cache key (address + metadata combination): the cached descriptor set and its hash
    private readonly Dictionary<string, CacheEntry> _descriptorCache = new(StringComparer.Ordinal);
    
    // Per connection ID: previous successful schema for rollback on error
    private readonly Dictionary<string, (ConnectionSchema Schema, string DescriptorHash)> _previousSchema = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Store a successful schema for rollback on subsequent fetch failure.
    /// </summary>
    public void StorePreviousSchema(string connectionId, ConnectionSchema schema, string descriptorHash)
    {
        lock (_previousSchema)
        {
            _previousSchema[connectionId] = (schema, descriptorHash);
        }
    }

    /// <summary>
    /// Attempt to retrieve the previous schema for rollback.
    /// </summary>
    public bool TryGetPreviousSchema(string connectionId, out ConnectionSchema schema, out string descriptorHash)
    {
        schema = null!;
        descriptorHash = null!;
        lock (_previousSchema)
        {
            if (!_previousSchema.TryGetValue(connectionId, out var entry))
                return false;
            schema = entry.Schema;
            descriptorHash = entry.DescriptorHash;
            return true;
        }
    }

    /// <summary>
    /// Clear the previous schema entry (e.g., after successful refresh).
    /// </summary>
    public void ClearPreviousSchema(string connectionId)
    {
        lock (_previousSchema)
        {
            _previousSchema.Remove(connectionId);
        }
    }

    /// <summary>
    /// Compute a SHA256 hash of the descriptor set for integrity verification.
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
