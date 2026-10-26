namespace RunPad.Services;

/// <summary>Maintains persisted recent-file order independently of a window or UI command.</summary>
public sealed class RecentFilesService
{
    public const int MaximumCount = 100;
    private readonly StringComparer _comparer;
    private readonly List<string> _paths;

    public RecentFilesService(IEnumerable<string> paths, StringComparer comparer)
    {
        _comparer = comparer;
        _paths = paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(comparer).Take(MaximumCount).ToList();
    }

    public IReadOnlyList<string> Paths => _paths;

    public void Remember(string path)
    {
        _paths.RemoveAll(existing => _comparer.Equals(existing, path));
        _paths.Insert(0, path);
        if (_paths.Count > MaximumCount) _paths.RemoveRange(MaximumCount, _paths.Count - MaximumCount);
    }

    public void Clear() => _paths.Clear();
}
