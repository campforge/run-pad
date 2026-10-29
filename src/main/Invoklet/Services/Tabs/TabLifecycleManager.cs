using System.Reactive.Linq;
using Invoklet.UserControls.Models;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace Invoklet.Services.Tabs;

/// <summary>
/// Owns when the view of an open tab exists. A tab is registered with its first view; the policy picks
/// the tabs that give up their view, which are replaced by a placeholder and built again when selected,
/// with the caret, scroll position and breakpoints they had. A tab is never unloaded while it is the
/// active one, while a script of it runs, after a run that ended while another tab was shown (until
/// the user visits it), while it is saving or loading, or while it has unsaved changes.
/// Everything happens on the UI thread.
/// </summary>
public sealed class TabLifecycleManager
{
    private sealed class Entry
    {
        public ITabView? View;
        public EditorViewState? SavedView;
        public bool UnseenResult;
        public bool Loading;
        public IDisposable? RunSubscription;
        public CancellationTokenSource? Load;
    }

    private readonly IResidencyPolicy _policy;
    private readonly ILogger<TabLifecycleManager>? _logger;
    private readonly Dictionary<EditorTabItemViewModel, Entry> _entries = new();
    private ITabSurface? _surface;
    private ITabViewFactory? _factory;
    private Func<EditorTabItemViewModel?>? _activeTab;

    public TabLifecycleManager(IResidencyPolicy policy, ILogger<TabLifecycleManager>? logger = null)
    {
        _policy = policy;
        _logger = logger;
    }

    internal void Attach(ITabSurface surface, ITabViewFactory factory, Func<EditorTabItemViewModel?> activeTab)
    {
        if (_surface is not null) throw new InvalidOperationException("The tab lifecycle manager is attached already.");
        (_surface, _factory, _activeTab) = (surface, factory, activeTab);
    }

    /// <summary>The strip went away: stops everything started for its tabs.</summary>
    internal void Detach()
    {
        foreach (var tab in _entries.Keys.ToList()) Forget(tab);
        (_surface, _factory, _activeTab) = (null, null, null);
    }

    internal bool IsLoaded(EditorTabItemViewModel tab) => _entries.TryGetValue(tab, out var entry) && entry.View is not null;

    /// <summary>A tab got its first view.</summary>
    internal void Register(EditorTabItemViewModel tab, ITabView view)
    {
        var entry = new Entry { View = view };
        entry.RunSubscription = tab.WhenAnyValue(x => x.RunState).Skip(1).Subscribe(state =>
        {
            if (state == RunState.Idle && !IsActive(tab)) entry.UnseenResult = true;
        });
        _entries[tab] = entry;
        _policy.Touch(tab);
    }

    /// <summary>The tab was closed: its view, if it has one, is disposed.</summary>
    internal void Unregister(EditorTabItemViewModel tab)
    {
        if (!_entries.TryGetValue(tab, out var entry)) return;
        Forget(tab);
        entry.View?.Dispose();
    }

    /// <summary>The user selected <paramref name="tab"/>: it is the one being used, so it is loaded if it was not.</summary>
    internal void Activated(EditorTabItemViewModel tab)
    {
        if (!_entries.TryGetValue(tab, out var entry)) return;
        _policy.Touch(tab);
        entry.UnseenResult = false;
        if (entry.View is null) _ = LoadAsync(tab, entry);
        else Rebalance();
    }

    /// <summary>Unloads the tabs the policy gives up and colours only the tab being looked at.</summary>
    internal void Rebalance()
    {
        if (_surface is null) return;
        var active = _activeTab?.Invoke();
        var loaded = _entries.Where(e => e.Value.View is not null).Select(e => e.Key).ToList();
        foreach (var tab in _policy.ColdTabs(loaded, tab => CanUnload(tab, _entries[tab], active)))
            Unload(tab, _entries[tab]);
        foreach (var (tab, entry) in _entries)
            entry.View?.SetHighlightingActive(ReferenceEquals(tab, active));
    }

    private bool IsActive(EditorTabItemViewModel tab) => ReferenceEquals(tab, _activeTab?.Invoke());

    private static bool CanUnload(EditorTabItemViewModel tab, Entry entry, EditorTabItemViewModel? active)
        => !ReferenceEquals(tab, active) && tab.RunState == RunState.Idle && !entry.UnseenResult && !entry.Loading
            && tab.PendingSave is not { IsCompleted: false };

    private void Unload(EditorTabItemViewModel tab, Entry entry)
    {
        if (entry.View is not { } view || view.HasRunningProcess) return;
        var snapshot = view.CaptureRecovery();
        // Unsaved work, in the text or in the metadata, stays in its editor.
        if (snapshot.IsDirty) return;
        // The session checkpoint still gets this tab's state, without a live editor to ask.
        tab.CaptureRecovery = () => snapshot;
        entry.SavedView = view.CaptureViewState();
        entry.View = null;
        _surface!.ShowPlaceholder(tab);
        view.Dispose();
    }

    private async Task LoadAsync(EditorTabItemViewModel tab, Entry entry)
    {
        if (_factory is null || entry.Loading) return;
        entry.Loading = true;
        using var load = new CancellationTokenSource();
        entry.Load = load;
        try
        {
            ITabView view;
            try
            {
                view = await _factory.CreateAsync(tab, load.Token);
            }
            catch (OperationCanceledException) when (load.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The tab stays unloaded, so selecting it again tries again.
                _logger?.LogWarning("Could not build the view of a tab being loaded ({ErrorType})", ex.GetType().Name);
                if (_surface is not null) await _surface.ReportLoadFailureAsync(tab, ex);
                return;
            }

            // Closed, or the strip went away, while the view was being built.
            if (_surface is null || !_entries.TryGetValue(tab, out var current) || !ReferenceEquals(current, entry))
            {
                view.Dispose();
                return;
            }

            try
            {
                if (entry.SavedView is { } saved) view.RestoreViewState(saved);
            }
            catch (Exception ex)
            {
                // Only the caret, scroll position and breakpoints are lost; the tab itself is fine.
                _logger?.LogWarning("Could not restore the view of a tab being loaded ({ErrorType})", ex.GetType().Name);
            }

            entry.SavedView = null;
            entry.View = view;
            _surface.ShowView(tab, view);
        }
        finally
        {
            entry.Loading = false;
            entry.Load = null;
            Rebalance();
        }
    }

    private void Forget(EditorTabItemViewModel tab)
    {
        if (!_entries.Remove(tab, out var entry)) return;
        entry.RunSubscription?.Dispose();
        entry.Load?.Cancel();
        _policy.Forget(tab);
    }
}
