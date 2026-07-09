using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DotPad.Services.BuildCache;

public sealed class BuildCacheService : IBuildCacheService
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DotPad", "build-cache.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly ILogger<BuildCacheService> _logger;
    private Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public BuildCacheService(ILogger<BuildCacheService> logger)
    {
        _logger = logger;
        Load();
    }

    public string EnsureOutputDirectory(string filePath, string workDir)
    {
        var key = NormalizeKey(filePath);
        if (_entries.TryGetValue(key, out var entry) && Directory.Exists(entry.Directory))
            return entry.Directory;

        var dir = Path.Combine(workDir, Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _entries[key] = new CacheEntry(dir, DateTimeOffset.UtcNow);
        Save();
        return dir;
    }

    public void Touch(string filePath)
    {
        var key = NormalizeKey(filePath);
        if (!_entries.TryGetValue(key, out var entry)) return;

        _entries[key] = entry with { LastUsedUtc = DateTimeOffset.UtcNow };
        Save();
    }

    public void PruneStale(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var stale = _entries.Where(kv => kv.Value.LastUsedUtc < cutoff).ToList();
        if (stale.Count == 0) return;

        foreach (var (key, entry) in stale)
        {
            try
            {
                if (Directory.Exists(entry.Directory))
                    Directory.Delete(entry.Directory, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete stale build cache directory {Directory}", entry.Directory);
            }

            _entries.Remove(key);
        }

        Save();
    }

    private static string NormalizeKey(string filePath) => Path.GetFullPath(filePath);

    private void Save()
    {
        var dir = Path.GetDirectoryName(CachePath)!;
        Directory.CreateDirectory(dir);
        var tmp = CachePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, SerializerOptions));
        File.Move(tmp, CachePath, overwrite: true);
    }

    private void Load()
    {
        if (!File.Exists(CachePath)) return;
        try
        {
            var json = File.ReadAllText(CachePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
            if (loaded is not null)
                _entries = new Dictionary<string, CacheEntry>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Build cache file is corrupt; starting empty. Path: {Path}", CachePath);
        }
    }

    private sealed record CacheEntry(string Directory, DateTimeOffset LastUsedUtc);
}
