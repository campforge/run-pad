using Invoklet.Contracts.Models;
using Invoklet.UserControls.Models;

namespace Invoklet.Services.Tabs;

/// <summary>What the commands on the active tab tell its view to do.</summary>
internal interface ITabCommandTarget
{
    Task RunAsync(bool debug, DotnetSdk platform);

    Task RunSelectionAsync(DotnetSdk platform);

    Task RestartAsync(DotnetSdk platform);

    void Cancel();

    void Pause();

    void RunToCursor();

    void ResumeDebug(DebugAction action);

    /// <summary>Writes the script to its file.</summary>
    void SaveInPlace();

    Task FormatAsync();

    Task ResolveAllUsingsAsync();

    void ShowGoToLine();
}

/// <summary>The tab being looked at: what the commands act on.</summary>
internal interface IActiveTabSource
{
    EditorTabItemViewModel? ViewModel { get; }

    /// <summary>The view of the active tab; null while the tab has none yet.</summary>
    ITabCommandTarget? Target { get; }
}
