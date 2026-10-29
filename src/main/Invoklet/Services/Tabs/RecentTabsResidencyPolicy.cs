namespace Invoklet.Services.Tabs;

/// <summary>
/// Keeps the few tabs the user used last. Every loaded tab holds an editor with its highlighting, an
/// IntelliSense workspace and a preview, so only <see cref="DefaultWarmTabs"/> of them stay loaded and the
/// rest show just their tab header until they are selected again.
/// </summary>
public sealed class RecentTabsResidencyPolicy : IResidencyPolicy
{
    public const int DefaultWarmTabs = 3;

    private readonly int _warmTabs;
    private readonly Dictionary<object, long> _lastUsed = new(ReferenceEqualityComparer.Instance);
    private long _clock;

    public RecentTabsResidencyPolicy(int warmTabs = DefaultWarmTabs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(warmTabs, 1);
        _warmTabs = warmTabs;
    }

    public void Touch(object tab) => _lastUsed[tab] = ++_clock;

    public void Forget(object tab) => _lastUsed.Remove(tab);

    public IReadOnlyList<T> ColdTabs<T>(IEnumerable<T> loaded, Func<T, bool> canUnload) where T : notnull
        => loaded.OrderByDescending(tab => _lastUsed.GetValueOrDefault(tab))
            .Skip(_warmTabs)
            .Where(canUnload)
            .ToList();
}
