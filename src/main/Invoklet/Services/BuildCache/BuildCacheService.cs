using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Invoklet.Services.BuildCache;

public sealed class BuildCacheService : IBuildCacheService
{
    private static readonly string DefaultCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Invoklet", "build-cache.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    // Guards _entries and every read/write of the backing file below — EnsureOutputDirectory/Touch
    // run on the UI thread while a tab opens/saves, and App.axaml.cs's startup PruneStale runs
    // concurrently on a background Task.Run; without this, concurrent mutation of the plain
    // Dictionary is undefined behavior.
    private readonly Lock _lock = new();
    private readonly ILogger<BuildCacheService> _logger;
    private readonly string _cachePath;
    private Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public BuildCacheService(ILogger<BuildCacheService> logger) : this(logger, DefaultCachePath) { }

    // Internal ctor with an injectable cache-file path so tests can point this at an isolated temp
    // file instead of the real %APPDATA%/Invoklet directory.
    internal BuildCacheService(ILogger<BuildCacheService> logger, string cachePath)
    {
        _logger = logger;
        _cachePath = cachePath;
        Load();
    }

    public string EnsureOutputDirectory(string filePath, string workDir)
    {
        var key = NormalizeKey(filePath);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) && Directory.Exists(entry.Directory))
                return entry.Directory;

            var dir = Path.Combine(workDir, Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            _entries[key] = new CacheEntry(dir, DateTimeOffset.UtcNow);
            Save();
            return dir;
        }
    }

    public void Touch(string filePath)
    {
        var key = NormalizeKey(filePath);
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry)) return;

            _entries[key] = entry with { LastUsedUtc = DateTimeOffset.UtcNow };
            Save();
        }
    }

    public void PruneStale(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        lock (_lock)
        {
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
    }

    private static string NormalizeKey(string filePath) => Path.GetFullPath(filePath);

    // Callers all hold _lock already — see EnsureOutputDirectory/Touch/PruneStale.
    private void Save()
    {
        var dir = Path.GetDirectoryName(_cachePath)!;
        Directory.CreateDirectory(dir);
        var tmp = _cachePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, SerializerOptions));
        File.Move(tmp, _cachePath, overwrite: true);
    }

    private void Load()
    {
        if (!File.Exists(_cachePath)) return;
        try
        {
            var json = File.ReadAllText(_cachePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
            if (loaded is not null)
                _entries = new Dictionary<string, CacheEntry>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Build cache file is corrupt; starting empty. Path: {Path}", _cachePath);
        }
    }

    private sealed record CacheEntry(string Directory, DateTimeOffset LastUsedUtc);
}
