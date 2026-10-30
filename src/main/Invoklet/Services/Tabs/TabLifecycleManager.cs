using System.Reactive.Linq;
using Invoklet.UserControls.Models;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace Invoklet.Services.Tabs;

/// <summary>
/// Owns which open tabs keep their text and output in memory. The control of a tab is light and stays; its
/// text, its output and the highlighting of its text are the heavy part. The policy picks the tabs that give
/// them up: they go to disk (<see cref="ITabSpillStore"/>) and come back when the tab is selected, or when
/// something needs the text (saving). A tab is never released while it is the active one, while a script of it
/// runs, after a run that ended while another tab was shown (until the user visits it), or while it is
/// saving. Everything happens on the UI thread.
/// </summary>
public sealed class TabLifecycleManager
{
    private sealed class Entry(ITabView view)
    {
        public ITabView View { get; } = view;
        public bool UnseenResult { get; set; }
        public IDisposable? RunSubscription { get; set; }
        public Task? Restoring { get; set; }
    }

    private readonly IResidencyPolicy _policy;
    private readonly ITabSpillStore _spill;
    private readonly ILogger<TabLifecycleManager>? _logger;
    private readonly Dictionary<EditorTabItemViewModel, Entry> _entries = new();
    private Func<EditorTabItemViewModel?>? _activeTab;
    private Action<EditorTabItemViewModel>? _contentNotRestored;

    public TabLifecycleManager(IResidencyPolicy policy, ITabSpillStore spill, ILogger<TabLifecycleManager>? logger = null)
    {
        _policy = policy;
        _spill = spill;
        _logger = logger;
    }

    internal ITabSpillStore Spill => _spill;

    /// <param name="activeTab">The tab being looked at.</param>
    /// <param name="contentNotRestored">A tab's text could not be brought back from disk.</param>
    internal void Attach(Func<EditorTabItemViewModel?> activeTab, Action<EditorTabItemViewModel> contentNotRestored)
    {
        if (_activeTab is not null) throw new InvalidOperationException("The tab lifecycle manager is attached already.");
        (_activeTab, _contentNotRestored) = (activeTab, contentNotRestored);
    }

    /// <summary>The strip went away: stops watching its tabs and removes what was kept for them.</summary>
    internal void Detach()
    {
        foreach (var tab in _entries.Keys.ToList()) Forget(tab);
        (_activeTab, _contentNotRestored) = (null, null);
        _spill.DeleteAll();
    }

    internal bool IsContentLoaded(EditorTabItemViewModel tab) => _entries.TryGetValue(tab, out var entry) && !entry.View.IsContentReleased;

    internal void Register(EditorTabItemViewModel tab, ITabView view)
    {
        var entry = new Entry(view);
        entry.RunSubscription = tab.WhenAnyValue(x => x.RunState).Skip(1).Subscribe(state =>
        {
            if (state == RunState.Idle && !IsActive(tab)) entry.UnseenResult = true;
        });
        _entries[tab] = entry;
        _policy.Touch(tab);
        tab.EnsureContent = () => EnsureContentAsync(tab);
    }

    /// <summary>The tab was closed. Its view is the strip's to dispose.</summary>
    internal void Unregister(EditorTabItemViewModel tab) => Forget(tab);

    /// <summary>The user selected <paramref name="tab"/>: it is the one being used, so its text is brought back.</summary>
    internal void Activated(EditorTabItemViewModel tab)
    {
        if (!_entries.TryGetValue(tab, out var entry)) return;
        _policy.Touch(tab);
        entry.UnseenResult = false;
        _ = RestoreAsync(tab, entry);
        Rebalance();
    }

    /// <summary>Completes once <paramref name="tab"/> has its text again (or once that failed).</summary>
    internal Task EnsureContentAsync(EditorTabItemViewModel tab)
    {
        if (!_entries.TryGetValue(tab, out var entry)) return Task.CompletedTask;
        // Something is using the tab, so it is not one to give up again as soon as its text is back.
        _policy.Touch(tab);
        return RestoreAsync(tab, entry);
    }

    /// <summary>Releases the content of the tabs the policy gives up and colours only the tab being looked at.</summary>
    internal void Rebalance()
    {
        var active = _activeTab?.Invoke();
        var loaded = _entries.Where(e => !e.Value.View.IsContentReleased).Select(e => e.Key).ToList();
        foreach (var tab in _policy.ColdTabs(loaded, tab => CanRelease(tab, _entries[tab], active)))
        {
            if (!_entries[tab].View.TryReleaseContent())
                _logger?.LogWarning("The content of a tab could not be released");
        }

        foreach (var (tab, entry) in _entries)
            entry.View.SetHighlightingActive(ReferenceEquals(tab, active));
    }

    // One restore at a time per tab: selecting it again, or saving it, while its content is being read waits for it.
    private Task RestoreAsync(EditorTabItemViewModel tab, Entry entry)
    {
        if (!entry.View.IsContentReleased) return Task.CompletedTask;
        if (entry.Restoring is { } running) return running;
        var restoring = RestoreCoreAsync(tab, entry);
        // Not when it already finished: its end has cleared the field, and a stale task would stay.
        if (!restoring.IsCompleted) entry.Restoring = restoring;
        return restoring;
    }

    private async Task RestoreCoreAsync(EditorTabItemViewModel tab, Entry entry)
    {
        try
        {
            if (await entry.View.TryRestoreContentAsync()) return;
            _logger?.LogWarning("The content of a tab could not be restored");
            _contentNotRestored?.Invoke(tab);
        }
        finally
        {
            entry.Restoring = null;
            Rebalance();
        }
    }

    private bool IsActive(EditorTabItemViewModel tab) => ReferenceEquals(tab, _activeTab?.Invoke());

    private static bool CanRelease(EditorTabItemViewModel tab, Entry entry, EditorTabItemViewModel? active)
        => !ReferenceEquals(tab, active) && tab.RunState == RunState.Idle && !entry.UnseenResult
            && tab.PendingSave is not { IsCompleted: false };

    private void Forget(EditorTabItemViewModel tab)
    {
        if (!_entries.Remove(tab, out var entry)) return;
        entry.RunSubscription?.Dispose();
        tab.EnsureContent = static () => Task.CompletedTask;
        _policy.Forget(tab);
    }
}
