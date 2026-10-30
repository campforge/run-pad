using Invoklet.Services.Tabs;
using Invoklet.UserControls.Models;

namespace Invoklet.UserControls;

// What the file sync asks of the strip.
public partial class EditorTabsControl
{
    bool ITabDiskHost.IsClosed => _disposed;

    IReadOnlyList<EditorTabItemViewModel> ITabDiskHost.Tabs => _vm?.Tabs ?? [];

    bool ITabDiskHost.HasContent(EditorTabItemViewModel tab) => HasControl(tab);

    DiskReload ITabDiskHost.Reload(EditorTabItemViewModel tab)
        => _tabs.TryGetValue(tab, out var entry) ? entry.Control.ReloadFromDisk() : DiskReload.Unavailable;

    void ITabDiskHost.Reopen(EditorTabItemViewModel tab) => _vm?.ReopenTab(tab);
}
