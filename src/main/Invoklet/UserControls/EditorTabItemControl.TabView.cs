using Invoklet.Services.Recovery;
using Invoklet.Services.Tabs;
using Invoklet.UserControls.Models;

namespace Invoklet.UserControls;

// What the tab lifecycle manager asks of a loaded tab; the members it forwards to are internal.
public partial class EditorsTabItemControl : ITabView
{
    bool ITabView.HasRunningProcess => HasRunningProcess;

    RecoveryTab ITabView.CaptureRecovery() => CaptureRecovery();

    EditorViewState ITabView.CaptureViewState() => CaptureViewState();

    void ITabView.RestoreViewState(EditorViewState state) => RestoreViewState(state);

    void ITabView.SetHighlightingActive(bool active) => SetHighlightingActive(active);
}
