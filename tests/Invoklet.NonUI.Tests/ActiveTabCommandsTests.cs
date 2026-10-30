using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Services.Tabs;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests;

// The commands of the keys on the tab being looked at, on fakes: no window, no editor.
public class ActiveTabCommandsTests
{
    private sealed class FakeTarget : ITabCommandTarget
    {
        public List<string> Calls { get; } = [];
        public Task RunAsync(bool debug, DotnetSdk platform) { Calls.Add($"run debug={debug} {platform.Name}"); return Task.CompletedTask; }
        public Task RunSelectionAsync(DotnetSdk platform) { Calls.Add("run selection " + platform.Name); return Task.CompletedTask; }
        public Task RestartAsync(DotnetSdk platform) { Calls.Add("restart " + platform.Name); return Task.CompletedTask; }
        public void Cancel() => Calls.Add("cancel");
        public void Pause() => Calls.Add("pause");
        public void RunToCursor() => Calls.Add("run to cursor");
        public void ResumeDebug(DebugAction action) => Calls.Add("resume " + action);
        public void SaveInPlace() => Calls.Add("save in place");
        public Task FormatAsync() { Calls.Add("format"); return Task.CompletedTask; }
        public Task ResolveAllUsingsAsync() { Calls.Add("usings"); return Task.CompletedTask; }
        public void ShowGoToLine() => Calls.Add("go to line");
    }

    private sealed class FakeSource : IActiveTabSource
    {
        public EditorTabItemViewModel? ViewModel { get; set; }
        public ITabCommandTarget? Target { get; set; }
    }

    private sealed class Harness
    {
        public FakeTarget Target { get; } = new();
        public FakeSource Source { get; } = new();
        public Mock<IEditorTabActions> Actions { get; } = new();
        public List<EditorTabItemViewModel> Reverted { get; } = [];
        public ActiveTabCommands Commands { get; }

        public Harness()
        {
            Source.Target = Target;
            Commands = new ActiveTabCommands(Source, () => null, tab => { Reverted.Add(tab); return Task.CompletedTask; });
        }

        public EditorTabItemViewModel Open(string? path = "/scripts/a.inv", bool withSdk = true)
        {
            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
            prefs.Setup(p => p.Session).Returns(new SessionSettings());
            List<DotnetSdk> sdks = withSdk ? [new DotnetSdk("net10", "net10.0", "10.0.0", "net10")] : [];
            var tab = new EditorTabItemViewModel(path, sdks, prefs.Object, Actions.Object, MdRunLanguage.CSharp, MdRunKind.Statements);
            Source.ViewModel = tab;
            return tab;
        }

        public async Task<bool> EventuallyAsync(Func<bool> condition)
        {
            for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
            return condition();
        }
    }

    [Fact]
    public void Bindings_Should_CoverEveryKeyOfTheActiveTab()
    {
        var keys = new Harness().Commands.Bindings.Keys.Select(k => k.Id).Order().ToList();

        Assert.Equal(new[]
        {
            KeyCommands.Save, KeyCommands.SaveAs, KeyCommands.RevertFile, KeyCommands.CloseTab, KeyCommands.ToggleOutputPanel,
            KeyCommands.TogglePreview, KeyCommands.TogglePreviewToSide, KeyCommands.ZoomIn, KeyCommands.ZoomOut,
            KeyCommands.ResetZoom, KeyCommands.RunOrContinue, KeyCommands.Stop, KeyCommands.ExecuteSelection,
            KeyCommands.Restart, KeyCommands.ToggleDebugMode, KeyCommands.StepOver, KeyCommands.StepInto,
            KeyCommands.StepOut, KeyCommands.Pause, KeyCommands.RunToCursor, KeyCommands.Format, KeyCommands.GoToLine,
            KeyCommands.ResolveAllUsings,
        }.Select(k => k.Id).Order(), keys);
    }

    [Fact]
    public void RunOrContinue_Should_Run_AnIdleTab_InItsDebugMode()
    {
        var h = new Harness();
        var tab = h.Open();
        tab.DebugModeEnabled = true;

        h.Commands.RunOrContinue();
        tab.DebugModeEnabled = false;
        h.Commands.RunOrContinue();

        Assert.Equal(["run debug=True net10", "run debug=False net10"], h.Target.Calls);
    }

    [Fact]
    public void RunOrContinue_Should_Continue_APausedTab_AndDoNothing_ForARunningOne()
    {
        var h = new Harness();
        var tab = h.Open();

        tab.RunState = RunState.Paused;
        h.Commands.RunOrContinue();
        tab.RunState = RunState.Run;
        h.Commands.RunOrContinue();

        Assert.Equal(["resume Continue"], h.Target.Calls);
    }

    [Fact]
    public void RunOrContinue_Should_DoNothing_WithoutAnSdk_OrWithoutATab()
    {
        var h = new Harness();
        h.Open(withSdk: false);
        h.Commands.RunOrContinue();
        h.Commands.ExecuteSelection();
        h.Commands.Restart();

        h.Source.ViewModel = null;
        h.Commands.RunOrContinue();
        h.Commands.Save();
        h.Commands.Cancel();

        Assert.Equal(["cancel"], h.Target.Calls); // only the command that needs no tab
    }

    [Fact]
    public void SteppingAndRunToCursor_Should_OnlyActAtAStop()
    {
        var h = new Harness();
        var tab = h.Open();

        h.Commands.Bindings[KeyCommands.StepOver]();
        h.Commands.Bindings[KeyCommands.StepInto]();
        h.Commands.Bindings[KeyCommands.StepOut]();
        h.Commands.RunToCursor();
        Assert.Empty(h.Target.Calls);

        tab.RunState = RunState.Paused;
        h.Commands.Bindings[KeyCommands.StepOver]();
        h.Commands.Bindings[KeyCommands.StepInto]();
        h.Commands.Bindings[KeyCommands.StepOut]();
        h.Commands.RunToCursor();

        Assert.Equal(["resume StepOver", "resume StepIn", "resume StepOut", "run to cursor"], h.Target.Calls);
    }

    [Fact]
    public void TheEditorCommands_Should_BeForwardedToTheTab()
    {
        var h = new Harness();
        h.Open();

        h.Commands.Bindings[KeyCommands.Format]();
        h.Commands.Bindings[KeyCommands.GoToLine]();
        h.Commands.Bindings[KeyCommands.ResolveAllUsings]();
        h.Commands.Bindings[KeyCommands.Pause]();
        h.Commands.ExecuteSelection();
        h.Commands.Restart();

        Assert.Equal(["format", "go to line", "usings", "pause", "run selection net10", "restart net10"], h.Target.Calls);
    }

    [Fact]
    public void ToggleDebugMode_Should_FlipTheTabsFlag()
    {
        var h = new Harness();
        var tab = h.Open();

        var before = tab.DebugModeEnabled;

        h.Commands.ToggleDebugMode();
        Assert.Equal(!before, tab.DebugModeEnabled);
        h.Commands.ToggleDebugMode();
        Assert.Equal(before, tab.DebugModeEnabled);
    }

    [Fact]
    public void Save_Should_WriteInPlace_ForATabWithAFile()
    {
        var h = new Harness();
        h.Open("/scripts/a.inv");

        h.Commands.Save();

        Assert.Equal(["save in place"], h.Target.Calls);
    }

    [Fact]
    public async Task Save_Should_GoThroughSaveAs_ForAnUntitledTab()
    {
        var h = new Harness();
        var tab = h.Open(path: null);

        h.Commands.Save();

        Assert.True(await h.EventuallyAsync(() => h.Actions.Invocations.Any(i => i.Method.Name == nameof(IEditorTabActions.SaveAsAsync))));
        Assert.Empty(h.Target.Calls);
        h.Actions.Verify(a => a.SaveAsAsync(tab), Times.Once);
    }

    [Fact]
    public async Task SaveAs_AndCloseTab_Should_UseTheTabsOwnCommands()
    {
        var h = new Harness();
        var tab = h.Open();

        h.Commands.SaveAs();
        h.Commands.CloseTab();

        Assert.True(await h.EventuallyAsync(() => h.Actions.Invocations.Count >= 2));
        h.Actions.Verify(a => a.SaveAsAsync(tab), Times.Once);
        h.Actions.Verify(a => a.CloseAsync(tab), Times.Once);
    }

    [Fact]
    public async Task WithoutAViewForTheTab_SaveAsAndCloseTab_Should_DoNothing()
    {
        var h = new Harness();
        h.Open();
        h.Source.Target = null;

        h.Commands.SaveAs();
        h.Commands.CloseTab();
        await h.Commands.RevertAsync();

        Assert.Empty(h.Actions.Invocations);
        Assert.Empty(h.Reverted);
    }

    [Fact]
    public async Task RevertAsync_Should_AskForTheActiveTabsFile_Only()
    {
        var h = new Harness();
        var tab = h.Open("/scripts/a.inv");

        await h.Commands.RevertAsync();
        h.Open(path: null);
        await h.Commands.RevertAsync();

        Assert.Equal([tab], h.Reverted);
    }

    [Fact]
    public void ZoomKeys_Should_DoNothing_WithoutPreferences()
    {
        var h = new Harness();

        h.Commands.Bindings[KeyCommands.ZoomIn]();
        h.Commands.Bindings[KeyCommands.ZoomOut]();
        h.Commands.Bindings[KeyCommands.ResetZoom]();
    }
}
