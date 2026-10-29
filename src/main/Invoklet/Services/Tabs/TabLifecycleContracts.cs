using Invoklet.Services.Recovery;
using Invoklet.UserControls.Models;

namespace Invoklet.Services.Tabs;

/// <summary>What the lifecycle manager needs from the view of a loaded tab.</summary>
internal interface ITabView : IDisposable
{
    /// <summary>A script of the tab is running or being debugged.</summary>
    bool HasRunningProcess { get; }

    RecoveryTab CaptureRecovery();

    EditorViewState CaptureViewState();

    void RestoreViewState(EditorViewState state);

    /// <summary>Only the view being looked at colours its text.</summary>
    void SetHighlightingActive(bool active);
}

/// <summary>Builds the view of a tab, with the editor services it needs.</summary>
internal interface ITabViewFactory
{
    Task<ITabView> CreateAsync(EditorTabItemViewModel tab, CancellationToken cancellationToken);
}

/// <summary>Where a tab's view is shown; the strip of tabs.</summary>
internal interface ITabSurface
{
    void ShowView(EditorTabItemViewModel tab, ITabView view);

    /// <summary>Stands in for the view of an unloaded tab, so nothing flashes while it is built again.</summary>
    void ShowPlaceholder(EditorTabItemViewModel tab);

    /// <summary>The view of an unloaded tab could not be built: the tab stays unloaded.</summary>
    Task ReportLoadFailureAsync(EditorTabItemViewModel tab, Exception error);
}
