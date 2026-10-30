using Invoklet.Contracts.Models;
using Invoklet.NonUI.Tests.App.Services.Tabs.Fixtures;
using Invoklet.Services.Preferences;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services.Tabs;

public sealed class ActiveTabCommandsTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Fact]
    public void Bindings_Created_CoverEveryKeyOfTheActiveTab()
    {
        // Arrange
        var sut = new ActiveTabCommandsFixture().CreateSut();
        KeyCommand[] expected =
        [
            KeyCommands.Save, KeyCommands.SaveAs, KeyCommands.ExportResults, KeyCommands.ClearResults, KeyCommands.RevertFile, KeyCommands.CloseTab, KeyCommands.ToggleOutputPanel,
            KeyCommands.TogglePreview, KeyCommands.TogglePreviewToSide, KeyCommands.ZoomIn, KeyCommands.ZoomOut,
            KeyCommands.ResetZoom, KeyCommands.RunOrContinue, KeyCommands.Stop, KeyCommands.ExecuteSelection,
            KeyCommands.Restart, KeyCommands.ToggleDebugMode, KeyCommands.StepOver, KeyCommands.StepInto,
            KeyCommands.StepOut, KeyCommands.Pause, KeyCommands.RunToCursor, KeyCommands.Format, KeyCommands.FormatSelection, KeyCommands.GoToLine,
            KeyCommands.ResolveAllUsings,
        ];

        // Act
        var keys = sut.Bindings.Keys.Select(k => k.Id).Order().ToList();

        // Assert
        Assert.Equal(expected.Select(k => k.Id).Order(), keys);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunOrContinue_IdleTabWithAnSdk_RunsItInItsDebugMode(bool debug)
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        var tab = fixture.CreateTab();
        tab.DebugModeEnabled = debug;
        fixture.Activate(tab);
        fixture.Target.Setup(t => t.RunAsync(It.IsAny<bool>(), It.IsAny<DotnetSdk>())).Returns(Task.CompletedTask);
        var sut = fixture.CreateSut();

        // Act
        sut.RunOrContinue();

        // Assert
        fixture.Target.Verify(t => t.RunAsync(debug, It.Is<DotnetSdk>(s => s.Name == "net10")), Times.Once);
        fixture.Target.VerifyNoOtherCalls();
    }

    [Fact]
    public void RunOrContinue_PausedTab_ContinuesTheDebugger()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        var tab = fixture.CreateTab();
        tab.RunState = RunState.Paused;
        fixture.Activate(tab);
        var sut = fixture.CreateSut();

        // Act
        sut.RunOrContinue();

        // Assert
        fixture.Target.Verify(t => t.ResumeDebug(DebugAction.Continue), Times.Once);
        fixture.Target.VerifyNoOtherCalls();
    }

    [Fact]
    public void RunOrContinue_RunningTab_DoesNothing()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        var tab = fixture.CreateTab();
        tab.RunState = RunState.Run;
        fixture.Activate(tab);
        var sut = fixture.CreateSut();

        // Act
        sut.RunOrContinue();

        // Assert
        fixture.Target.VerifyNoOtherCalls();
    }

    [Fact]
    public void RunCommands_TabWithoutAnSdk_DoNothing()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        fixture.Activate(fixture.CreateTab(withSdk: false));
        var sut = fixture.CreateSut();

        // Act
        sut.RunOrContinue();
        sut.ExecuteSelection();
        sut.Restart();

        // Assert
        fixture.Target.VerifyNoOtherCalls();
    }

    [Fact]
    public void Commands_NoActiveTab_DoNothing()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        fixture.Activate(null);
        var sut = fixture.CreateSut();

        // Act
        sut.RunOrContinue();
        sut.ExecuteSelection();
        sut.Restart();
        sut.Save();
        sut.SaveAs();
        sut.CloseTab();
        sut.ToggleDebugMode();
        sut.RunToCursor();

        // Assert
        fixture.Target.VerifyNoOtherCalls();
        fixture.TabActions.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("run.stepOver", DebugAction.StepOver)]
    [InlineData("run.stepInto", DebugAction.StepIn)]
    [InlineData("run.stepOut", DebugAction.StepOut)]
    public void Step_PausedTab_ResumesWithThatStep(string key, DebugAction expected)
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        var tab = fixture.CreateTab();
        tab.RunState = RunState.Paused;
        fixture.Activate(tab);
        var sut = fixture.CreateSut();

        // Act
        sut.Bindings.Single(b => KeyOf(b.Key) == key).Value();

        // Assert
        fixture.Target.Verify(t => t.ResumeDebug(expected), Times.Once);
        fixture.Target.VerifyNoOtherCalls();
    }

    [Fact]
    public void Step_TabNotPaused_DoesNothing()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        fixture.Activate(fixture.CreateTab());
        var sut = fixture.CreateSut();

        // Act
        sut.Bindings[KeyCommands.StepOver]();
        sut.Bindings[KeyCommands.StepInto]();
        sut.Bindings[KeyCommands.StepOut]();
        sut.RunToCursor();

        // Assert
        fixture.Target.VerifyNoOtherCalls();
    }

    [Fact]
    public void RunToCursor_PausedTab_RunsToTheCursor()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        var tab = fixture.CreateTab();
        tab.RunState = RunState.Paused;
        fixture.Activate(tab);
        var sut = fixture.CreateSut();

        // Act
        sut.RunToCursor();

        // Assert
        fixture.Target.Verify(t => t.RunToCursor(), Times.Once);
    }

    [Fact]
    public void EditorCommands_ActiveTab_AreForwardedToItsView()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        fixture.Activate(fixture.CreateTab());
        fixture.Target.Setup(t => t.FormatAsync()).Returns(Task.CompletedTask);
        fixture.Target.Setup(t => t.FormatSelectionAsync()).Returns(Task.CompletedTask);
        fixture.Target.Setup(t => t.ExportResultsAsync()).Returns(Task.CompletedTask);
        fixture.Target.Setup(t => t.ResolveAllUsingsAsync()).Returns(Task.CompletedTask);
        fixture.Target.Setup(t => t.RunSelectionAsync(It.IsAny<DotnetSdk>())).Returns(Task.CompletedTask);
        fixture.Target.Setup(t => t.RestartAsync(It.IsAny<DotnetSdk>())).Returns(Task.CompletedTask);
        var sut = fixture.CreateSut();

        // Act
        sut.Bindings[KeyCommands.Format]();
        sut.Bindings[KeyCommands.FormatSelection]();
        sut.Bindings[KeyCommands.ExportResults]();
        sut.Bindings[KeyCommands.ClearResults]();
        sut.Bindings[KeyCommands.GoToLine]();
        sut.Bindings[KeyCommands.ResolveAllUsings]();
        sut.Bindings[KeyCommands.Pause]();
        sut.Bindings[KeyCommands.Stop]();
        sut.ExecuteSelection();
        sut.Restart();

        // Assert
        fixture.Target.Verify(t => t.FormatAsync(), Times.Once);
        fixture.Target.Verify(t => t.FormatSelectionAsync(), Times.Once);
        fixture.Target.Verify(t => t.ExportResultsAsync(), Times.Once);
        fixture.Target.Verify(t => t.ClearResults(), Times.Once);
        fixture.Target.Verify(t => t.ShowGoToLine(), Times.Once);
        fixture.Target.Verify(t => t.ResolveAllUsingsAsync(), Times.Once);
        fixture.Target.Verify(t => t.Pause(), Times.Once);
        fixture.Target.Verify(t => t.Cancel(), Times.Once);
        fixture.Target.Verify(t => t.RunSelectionAsync(It.Is<DotnetSdk>(s => s.Name == "net10")), Times.Once);
        fixture.Target.Verify(t => t.RestartAsync(It.Is<DotnetSdk>(s => s.Name == "net10")), Times.Once);
    }

    [Fact]
    public void ToggleDebugMode_Called_FlipsTheTabsFlagBackAndForth()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        var tab = fixture.CreateTab();
        fixture.Activate(tab);
        var sut = fixture.CreateSut();
        var before = tab.DebugModeEnabled;

        // Act
        sut.ToggleDebugMode();
        var flipped = tab.DebugModeEnabled;
        sut.ToggleDebugMode();

        // Assert
        Assert.Equal(!before, flipped);
        Assert.Equal(before, tab.DebugModeEnabled);
    }

    [Fact]
    public void Save_TabWithAFile_WritesItInPlace()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        fixture.Activate(fixture.CreateTab("/scripts/a.inv"));
        var sut = fixture.CreateSut();

        // Act
        sut.Save();

        // Assert
        fixture.Target.Verify(t => t.SaveInPlace(), Times.Once);
        fixture.TabActions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Save_UntitledTab_GoesThroughSaveAs()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        var tab = fixture.CreateTab(path: null);
        fixture.Activate(tab);
        var sut = fixture.CreateSut();

        // Act
        sut.Save();
        await fixture.SaveAsRequested.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        fixture.TabActions.Verify(a => a.SaveAsAsync(tab), Times.Once);
        fixture.Target.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SaveAs_ActiveTab_UsesTheTabsOwnCommand()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        var tab = fixture.CreateTab();
        fixture.Activate(tab);
        var sut = fixture.CreateSut();

        // Act
        sut.SaveAs();
        await fixture.SaveAsRequested.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        fixture.TabActions.Verify(a => a.SaveAsAsync(tab), Times.Once);
    }

    [Fact]
    public async Task CloseTab_ActiveTab_UsesTheTabsOwnCloseCommand()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        var tab = fixture.CreateTab();
        fixture.Activate(tab);
        var sut = fixture.CreateSut();

        // Act
        sut.CloseTab();
        await fixture.CloseRequested.WaitAsync(Limit, TestContext.Current.CancellationToken);

        // Assert
        fixture.TabActions.Verify(a => a.CloseAsync(tab), Times.Once);
    }

    [Fact]
    public async Task SaveAsCloseTabAndRevert_TabWithoutAView_DoNothing()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        fixture.Activate(fixture.CreateTab(), withView: false);
        var sut = fixture.CreateSut();

        // Act
        sut.SaveAs();
        sut.CloseTab();
        await sut.RevertAsync();

        // Assert
        fixture.TabActions.VerifyNoOtherCalls();
        fixture.Reverter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RevertAsync_TabWithAFile_AsksTheReverterForThatTab()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        var tab = fixture.CreateTab("/scripts/a.inv");
        fixture.Activate(tab);
        var sut = fixture.CreateSut();

        // Act
        await sut.RevertAsync();

        // Assert
        fixture.Reverter.Verify(r => r.RevertAsync(tab), Times.Once);
    }

    [Fact]
    public async Task RevertAsync_UntitledTab_DoesNotAskTheReverter()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        fixture.Activate(fixture.CreateTab(path: null));
        var sut = fixture.CreateSut();

        // Act
        await sut.RevertAsync();

        // Assert
        fixture.Reverter.VerifyNoOtherCalls();
    }

    [Fact]
    public void ZoomKeys_ACodeZoomExists_DelegateToIt()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        fixture.Activate(null);
        fixture.ProvideCodeZoom();
        var sut = fixture.CreateSut();

        // Act
        sut.Bindings[KeyCommands.ZoomIn]();
        sut.Bindings[KeyCommands.ZoomOut]();
        sut.Bindings[KeyCommands.ResetZoom]();

        // Assert
        fixture.Zoom.Verify(z => z.ZoomIn(), Times.Once);
        fixture.Zoom.Verify(z => z.ZoomOut(), Times.Once);
        fixture.Zoom.Verify(z => z.ResetCodeZoom(), Times.Once);
    }

    [Fact]
    public void ZoomKeys_NoCodeZoom_DoNothing()
    {
        // Arrange
        var fixture = new ActiveTabCommandsFixture();
        fixture.Activate(null);
        var sut = fixture.CreateSut();

        // Act
        sut.Bindings[KeyCommands.ZoomIn]();

        // Assert
        fixture.Zoom.VerifyNoOtherCalls();
    }

    private static string KeyOf(KeyCommand command) => command.Id;
}
