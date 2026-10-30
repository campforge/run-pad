using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Controls;
using ReactiveUI;

namespace Invoklet.Shared;

// MainWindowViewModel.SidebarColumnWidth/ToggleSidebar and
// EditorTabItemViewModel.OutputPanelHeight/ToggleOutputPanel duplicated the exact same
// collapse/expand-plus-throttled-persist scheme almost line for line. This is that scheme,
// extracted once — the two call sites now just supply their own saved/default size and a callback
// that writes into their own IPreferencesService.Layout.* fields (kept out of this class entirely,
// so it has no dependency on preferences and is trivially constructible in isolation).
public sealed class CollapsiblePanelState : ReactiveObject
{
    private static readonly TimeSpan DefaultSaveThrottle = TimeSpan.FromMilliseconds(400);

    private readonly double _defaultExpandedSize;
    private double _lastExpandedSize;

    public GridLength Size
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    /// <param name="collapsed">Whether the panel starts collapsed.</param>
    /// <param name="savedSize">The last persisted expanded size (0 if never expanded before).</param>
    /// <param name="defaultExpandedSize">Fallback size to expand to when <paramref name="savedSize"/> is 0.</param>
    /// <param name="onChanged">
    /// Called (throttled) once a change settles. The first argument is whether the new size is
    /// collapsed (0); the second is the raw new size — only meaningful, and only worth persisting,
    /// when the first argument is <see langword="false"/> (mirrors the original call sites' own
    /// "don't overwrite the remembered expanded size with 0 while collapsing" behavior).
    /// </param>
    /// <param name="saveThrottle">How long a change must stay still before <paramref name="onChanged"/> is called; a test gives zero.</param>
    public CollapsiblePanelState(bool collapsed, double savedSize, double defaultExpandedSize, Action<bool, double> onChanged,
        TimeSpan? saveThrottle = null)
    {
        _defaultExpandedSize = defaultExpandedSize;
        _lastExpandedSize = savedSize;
        Size = collapsed ? new GridLength(0) : new GridLength(savedSize);

        ToggleCommand = ReactiveCommand.Create(Toggle);

        this.WhenAnyValue(x => x.Size)
            .Skip(1)
            .Throttle(saveThrottle ?? DefaultSaveThrottle, RxApp.MainThreadScheduler)
            .Subscribe(w => onChanged(w.Value <= 0, w.Value));
    }

    private void Toggle()
    {
        if (Size.Value > 0)
        {
            _lastExpandedSize = Size.Value;
            Size = new GridLength(0);
        }
        else
        {
            Size = new GridLength(_lastExpandedSize > 0 ? _lastExpandedSize : _defaultExpandedSize);
        }
    }
}
