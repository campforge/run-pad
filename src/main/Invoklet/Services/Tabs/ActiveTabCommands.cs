using Invoklet.Contracts.Models;
using Invoklet.Services.Preferences;
using Invoklet.UserControls.Models;

namespace Invoklet.Services.Tabs;

/// <summary>
/// The commands of the keys and the menu that act on the tab being looked at: run and debug, save, format,
/// close, zoom. <see cref="Bindings"/> is the one table of them, so the window that binds the keys and the strip
/// of tabs know nothing of the individual commands. A command with nothing to act on does nothing.
/// </summary>
internal sealed class ActiveTabCommands
{
    private readonly IActiveTabSource _active;
    private readonly ITabReverter _reverter;

    internal ActiveTabCommands(IActiveTabSource active, ITabReverter reverter)
    {
        _active = active;
        _reverter = reverter;
        Bindings = new Dictionary<KeyCommand, Action>
        {
            [KeyCommands.Save] = Save,
            [KeyCommands.SaveAs] = SaveAs,
            [KeyCommands.RevertFile] = () => _ = RevertAsync(),
            [KeyCommands.CloseTab] = CloseTab,
            [KeyCommands.ToggleOutputPanel] = ToggleOutputPanel,
            [KeyCommands.TogglePreview] = TogglePreview,
            [KeyCommands.TogglePreviewToSide] = TogglePreviewToSide,
            [KeyCommands.ZoomIn] = () => _active.CodeZoom?.ZoomIn(),
            [KeyCommands.ZoomOut] = () => _active.CodeZoom?.ZoomOut(),
            [KeyCommands.ResetZoom] = () => _active.CodeZoom?.ResetCodeZoom(),
            [KeyCommands.RunOrContinue] = RunOrContinue,
            [KeyCommands.Stop] = Cancel,
            [KeyCommands.ExecuteSelection] = ExecuteSelection,
            [KeyCommands.Restart] = Restart,
            [KeyCommands.ToggleDebugMode] = ToggleDebugMode,
            [KeyCommands.StepOver] = () => ResumeWhenPaused(DebugAction.StepOver),
            [KeyCommands.StepInto] = () => ResumeWhenPaused(DebugAction.StepIn),
            [KeyCommands.StepOut] = () => ResumeWhenPaused(DebugAction.StepOut),
            [KeyCommands.Pause] = () => _active.Target?.Pause(),
            [KeyCommands.RunToCursor] = RunToCursor,
            [KeyCommands.Format] = () => _ = _active.Target?.FormatAsync(),
            [KeyCommands.GoToLine] = () => _active.Target?.ShowGoToLine(),
            [KeyCommands.ResolveAllUsings] = () => _ = _active.Target?.ResolveAllUsingsAsync(),
        };
    }

    /// <summary>The command of each key, for the window to bind.</summary>
    internal IReadOnlyDictionary<KeyCommand, Action> Bindings { get; }

    private static DotnetSdk? SdkOf(EditorTabItemViewModel tab) => tab.SelectedSdk ?? tab.AvailableSdks.FirstOrDefault();

    internal void RunOrContinue()
    {
        if (_active.Target is not { } target || _active.ViewModel is not { } tab) return;
        if (tab.EnableRunButton)
        {
            if (SdkOf(tab) is { } platform)
                _ = target.RunAsync(tab.DebugModeEnabled, platform);
        }
        else if (tab.IsPaused)
            target.ResumeDebug(DebugAction.Continue);
    }

    internal void ExecuteSelection()
    {
        if (_active.Target is not { } target || _active.ViewModel is not { } tab) return;
        if (SdkOf(tab) is { } sdk) _ = target.RunSelectionAsync(sdk);
    }

    internal void Restart()
    {
        if (_active.Target is not { } target || _active.ViewModel is not { } tab) return;
        if (SdkOf(tab) is { } sdk) _ = target.RestartAsync(sdk);
    }

    internal void Cancel() => _active.Target?.Cancel();

    // Stepping only makes sense at a stop.
    private void ResumeWhenPaused(DebugAction action)
    {
        if (_active.ViewModel?.IsPaused == true) _active.Target?.ResumeDebug(action);
    }

    internal void RunToCursor()
    {
        if (_active.ViewModel?.IsPaused == true) _active.Target?.RunToCursor();
    }

    // The toolbar's bug button binds to the same flag.
    internal void ToggleDebugMode()
    {
        if (_active.ViewModel is { } tab) tab.DebugModeEnabled = !tab.DebugModeEnabled;
    }

    internal void ToggleOutputPanel() => _active.ViewModel?.OutputPanel.ToggleCommand.Execute().Subscribe();

    internal void TogglePreview() => _active.ViewModel?.TogglePreview();

    internal void TogglePreviewToSide() => _active.ViewModel?.TogglePreviewToSide();

    // Explicit Save: a tab with a path writes in place; an untitled one has nowhere to write to yet, so it goes
    // through the same Save As flow as the tab's context menu.
    internal void Save()
    {
        if (_active.Target is not { } target || _active.ViewModel is not { } tab) return;
        if (tab.FilePath is not null)
            target.SaveInPlace();
        else
            tab.SaveAsCommand.Execute().Subscribe();
    }

    // Always opens the Save As dialog, even for a tab that already has a path.
    internal void SaveAs()
    {
        if (_active.ViewModel is not { } tab || _active.Target is null) return;
        tab.SaveAsCommand.Execute().Subscribe();
    }

    // Goes through the tab's own close command, so the close is confirmed as the tab's [x] does.
    internal void CloseTab()
    {
        if (_active.ViewModel is not { } tab || _active.Target is null) return;
        tab.CloseCommand.Execute().Subscribe();
    }

    /// <summary>The active tab goes back to its file as it is on disk, asking first when that drops unsaved changes.</summary>
    internal Task RevertAsync()
        => _active.ViewModel is { FilePath: not null } tab && _active.Target is not null ? _reverter.RevertAsync(tab) : Task.CompletedTask;
}
