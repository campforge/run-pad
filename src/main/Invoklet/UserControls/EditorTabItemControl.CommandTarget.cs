using Invoklet.Contracts.Models;
using Invoklet.Services.Tabs;

namespace Invoklet.UserControls;

// What the commands on the active tab ask of it; the members it forwards to are internal.
public partial class EditorsTabItemControl : ITabCommandTarget
{
    Task ITabCommandTarget.RunAsync(bool debug, DotnetSdk platform) => RunAsync(debug, platform);

    Task ITabCommandTarget.RunSelectionAsync(DotnetSdk platform) => RunSelectionAsync(platform);

    Task ITabCommandTarget.RestartAsync(DotnetSdk platform) => RestartAsync(platform);

    void ITabCommandTarget.Cancel() => Cancel();

    void ITabCommandTarget.Pause() => Pause();

    void ITabCommandTarget.RunToCursor() => RunToCursor();

    void ITabCommandTarget.ResumeDebug(DebugAction action) => ResumeDebug(action);

    void ITabCommandTarget.SaveInPlace() => SaveIfNeeded();

    Task ITabCommandTarget.FormatAsync() => FormatAsync();

    Task ITabCommandTarget.FormatSelectionAsync() => _formatting?.FormatSelectionAsync() ?? Task.CompletedTask;

    Task ITabCommandTarget.ResolveAllUsingsAsync() => ResolveAllUsingsAsync();

    void ITabCommandTarget.ShowGoToLine() => ShowGoToLine();
}
