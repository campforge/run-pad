using Invoklet.Contracts.Models;
using Invoklet.Services.Scaling;
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

    Task FormatSelectionAsync();

    /// <summary>Saves the results panel as an HTML file.</summary>
    Task ExportResultsAsync();

    Task ResolveAllUsingsAsync();

    void ShowGoToLine();
}

/// <summary>The tab being looked at: what the commands act on.</summary>
internal interface IActiveTabSource
{
    EditorTabItemViewModel? ViewModel { get; }

    /// <summary>The view of the active tab; null while the tab has none yet.</summary>
    ITabCommandTarget? Target { get; }

    /// <summary>The code zoom, which also works with no tab; null before preferences exist.</summary>
    ICodeZoom? CodeZoom { get; }
}

/// <summary>Takes a tab back to its file as it is on disk, asking first when that drops unsaved changes.</summary>
internal interface ITabReverter
{
    Task RevertAsync(EditorTabItemViewModel tab);
}
