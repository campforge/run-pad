namespace Invoklet.Services;

/// <summary>
/// Which open tabs keep their editor in memory. Every loaded tab holds an editor with its highlighting,
/// an IntelliSense workspace and a preview, so only the few the user touched last stay loaded and the
/// rest show just their tab header until they are selected again.
/// </summary>
public sealed class TabResidencyManager
{
    public const int DefaultWarmTabs = 3;

    private readonly int _warmTabs;
    private readonly Dictionary<object, long> _lastUsed = new(ReferenceEqualityComparer.Instance);
    private long _clock;

    public TabResidencyManager(int warmTabs = DefaultWarmTabs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(warmTabs, 1);
        _warmTabs = warmTabs;
    }

    /// <summary>The user opened or selected <paramref name="tab"/>.</summary>
    public void Touch(object tab) => _lastUsed[tab] = ++_clock;

    public void Forget(object tab) => _lastUsed.Remove(tab);

    /// <summary>
    /// Of the <paramref name="loaded"/> tabs, those to unload: everything but the most recently used
    /// <see cref="DefaultWarmTabs"/>, and only the ones <paramref name="canUnload"/> accepts (a tab that
    /// is running or holds unsaved work stays, however long ago it was used).
    /// </summary>
    public IReadOnlyList<T> ColdTabs<T>(IEnumerable<T> loaded, Func<T, bool> canUnload) where T : notnull
        => loaded.OrderByDescending(tab => _lastUsed.GetValueOrDefault(tab))
            .Skip(_warmTabs)
            .Where(canUnload)
            .ToList();
}
