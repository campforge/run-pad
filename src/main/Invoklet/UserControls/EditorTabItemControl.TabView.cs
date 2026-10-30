using Invoklet.Services.Tabs;

namespace Invoklet.UserControls;

// What the tab lifecycle manager asks of a tab; the members it forwards to are internal.
public partial class EditorsTabItemControl : ITabView
{
    bool ITabView.HasRunningProcess => HasRunningProcess;

    bool ITabView.IsContentReleased => IsContentReleased;

    bool ITabView.TryReleaseContent() => TryReleaseContent();

    Task<bool> ITabView.TryRestoreContentAsync() => TryRestoreContentAsync();

    void ITabView.SetHighlightingActive(bool active) => SetHighlightingActive(active);
}
