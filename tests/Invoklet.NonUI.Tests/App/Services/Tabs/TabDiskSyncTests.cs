using Invoklet.NonUI.Tests.App.Services.Tabs.Fixtures;
using Invoklet.Services;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services.Tabs;

public sealed class TabDiskSyncTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CheckAsync_FileDidNotChange_LeavesTheTabAlone()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        fixture.CreateTab("a", onDisk: 1);
        var sut = fixture.CreateSut();

        // Act
        await sut.CheckAsync(fixture.Host.Object, Token);

        // Assert
        fixture.Host.Verify(h => h.Reload(It.IsAny<EditorTabItemViewModel>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_FileChangedAndTabIsClean_ReloadsItWithoutAsking()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        var tab = fixture.CreateTab("a");
        var sut = fixture.CreateSut();

        // Act
        await sut.CheckAsync(fixture.Host.Object, Token);

        // Assert
        fixture.Host.Verify(h => h.Reload(tab), Times.Once);
        fixture.Windows.Verify(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_FileChangedAndTabHasUnsavedChanges_AsksBeforeReloading()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        var tab = fixture.CreateTab("a", dirty: true);
        var sut = fixture.CreateSut();

        // Act
        await sut.CheckAsync(fixture.Host.Object, Token);

        // Assert
        fixture.Windows.Verify(w => w.ShowReloadChangedFileDialogAsync("a.inv"), Times.Once);
        fixture.Host.Verify(h => h.Reload(tab), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_ReloadDeclined_AsksAgainOnlyAboutANewerVersionOfTheFile()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        var tab = fixture.CreateTab("a", dirty: true);
        fixture.Windows.Setup(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>())).ReturnsAsync(false);
        var sut = fixture.CreateSut();

        // Act
        await sut.CheckAsync(fixture.Host.Object, Token);
        await sut.CheckAsync(fixture.Host.Object, Token);
        var askedForTheSameVersion = fixture.Windows.Invocations.Count(i => i.Method.Name == nameof(IWindowService.ShowReloadChangedFileDialogAsync));
        fixture.SetDiskState(tab, 3);
        await sut.CheckAsync(fixture.Host.Object, Token);

        // Assert
        Assert.Equal(1, askedForTheSameVersion);
        fixture.Windows.Verify(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>()), Times.Exactly(2));
        fixture.Host.Verify(h => h.Reload(It.IsAny<EditorTabItemViewModel>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_TabIsRunning_LeavesItAlone()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        fixture.CreateTab("a").RunState = RunState.Run;
        var sut = fixture.CreateSut();

        // Act
        await sut.CheckAsync(fixture.Host.Object, Token);

        // Assert
        fixture.Host.Verify(h => h.Reload(It.IsAny<EditorTabItemViewModel>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_TabsTextIsOnDisk_DoesNotReloadIt()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        fixture.RemoveContent(fixture.CreateTab("a"));
        var sut = fixture.CreateSut();

        // Act
        await sut.CheckAsync(fixture.Host.Object, Token);

        // Assert
        fixture.Host.Verify(h => h.Reload(It.IsAny<EditorTabItemViewModel>()), Times.Never);
        fixture.Reader.Verify(r => r.Read(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_FileCannotBeRead_SkipsItAndChecksTheOthers()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        var locked = fixture.CreateTab("a");
        var other = fixture.CreateTab("b");
        fixture.Reader.Setup(r => r.Read(locked.FilePath!)).Throws(new IOException("locked"));
        var sut = fixture.CreateSut();

        // Act
        await sut.CheckAsync(fixture.Host.Object, Token);

        // Assert
        fixture.Host.Verify(h => h.Reload(other), Times.Once);
        fixture.Host.Verify(h => h.Reload(locked), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_TabLeftWhileTheQuestionWasOpen_DoesNotReloadIt()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        var tab = fixture.CreateTab("a", dirty: true);
        fixture.Windows.Setup(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>()))
            .Returns(() =>
            {
                fixture.RemoveContent(tab); // the tab goes away behind the dialog
                return Task.FromResult(true);
            });
        var sut = fixture.CreateSut();

        // Act
        await sut.CheckAsync(fixture.Host.Object, Token);

        // Assert
        fixture.Host.Verify(h => h.Reload(It.IsAny<EditorTabItemViewModel>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_ScriptIsNowInAnotherLanguage_OpensItInANewTab()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        var tab = fixture.CreateTab("a");
        fixture.Host.Setup(h => h.Reload(tab)).Returns(DiskReload.OtherLanguage);
        var sut = fixture.CreateSut();

        // Act
        await sut.CheckAsync(fixture.Host.Object, Token);

        // Assert
        fixture.Host.Verify(h => h.Reopen(tab), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_FileVanishedWhileReloading_ShowsAnError()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        var tab = fixture.CreateTab("a");
        fixture.Host.Setup(h => h.Reload(tab)).Returns(DiskReload.Unavailable);
        var sut = fixture.CreateSut();

        // Act
        await sut.CheckAsync(fixture.Host.Object, Token);

        // Assert
        fixture.Windows.Verify(w => w.ShowErrorAsync("Reload Failed", "Could not read 'a.inv'."), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_AskedAgainWhileAQuestionIsOpen_ChecksOnceMoreAfterIt()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        var tab = fixture.CreateTab("a", dirty: true);
        var gate = new TaskCompletionSource<bool>();
        var asked = 0;
        fixture.Windows.Setup(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>()))
            .Returns(() => ++asked == 1 ? gate.Task : Task.FromResult(false));
        var sut = fixture.CreateSut();

        // Act
        var first = sut.CheckAsync(fixture.Host.Object, Token);
        await sut.CheckAsync(fixture.Host.Object, Token); // returns at once: noted for later
        fixture.SetDiskState(tab, 3);
        gate.SetResult(true);
        await first.WaitAsync(Limit, Token);

        // Assert
        Assert.Equal(2, asked); // the second pass looked at the newer version
    }

    [Fact]
    public async Task CheckAsync_StripIsClosed_StopsTheCheck()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        fixture.CreateTab("a");
        fixture.IsClosed = true;
        var sut = fixture.CreateSut();

        // Act
        await sut.CheckAsync(fixture.Host.Object, Token);

        // Assert
        fixture.Host.Verify(h => h.Reload(It.IsAny<EditorTabItemViewModel>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_CancelledWhileFilesAreBeingRead_DoesNotReadTheRest()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        for (var i = 0; i < 20; i++) fixture.CreateTab("tab" + i);
        using var cts = new CancellationTokenSource();
        var reads = 0;
        fixture.Reader.Setup(r => r.Read(It.IsAny<string>())).Returns(() =>
        {
            Interlocked.Increment(ref reads);
            cts.Cancel(); // the window is closed while the first file is read
            return TabDiskSyncFixture.State(2);
        });
        var sut = fixture.CreateSut();

        // Act
        await sut.CheckAsync(fixture.Host.Object, cts.Token);

        // Assert
        Assert.Equal(1, Volatile.Read(ref reads));
        fixture.Host.Verify(h => h.Reload(It.IsAny<EditorTabItemViewModel>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_CancelledDuringAReadThatHangs_DoesNotWaitForIt()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        fixture.CreateTab("a");
        using var cts = new CancellationTokenSource();
        using var hang = new ManualResetEventSlim();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Reader.Setup(r => r.Read(It.IsAny<string>())).Returns(() =>
        {
            readStarted.SetResult();
            hang.Wait(TimeSpan.FromSeconds(30)); // a file on a drive that does not answer
            return TabDiskSyncFixture.State(2);
        });
        var sut = fixture.CreateSut();

        try
        {
            // Act
            var check = sut.CheckAsync(fixture.Host.Object, cts.Token);
            await readStarted.Task.WaitAsync(Limit, Token); // the read really has begun
            cts.Cancel();
            await check.WaitAsync(Limit, Token); // returns long before the read would

            // Assert
            Assert.True(check.IsCompletedSuccessfully);
            fixture.Host.Verify(h => h.Reload(It.IsAny<EditorTabItemViewModel>()), Times.Never);
        }
        finally { hang.Set(); }
    }

    [Fact]
    public async Task RevertAsync_TabIsClean_ReloadsItWithoutAsking()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        var tab = fixture.CreateTab("a");
        fixture.ActiveTab = tab;
        var sut = fixture.CreateSut();

        // Act
        await sut.RevertAsync(fixture.Host.Object, tab);

        // Assert
        fixture.Host.Verify(h => h.Reload(tab), Times.Once);
        fixture.Windows.Verify(w => w.ShowRevertFileDialogAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RevertAsync_TabHasUnsavedChangesAndUserDeclines_DoesNotReload()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        var tab = fixture.CreateTab("a", dirty: true);
        fixture.ActiveTab = tab;
        fixture.Windows.Setup(w => w.ShowRevertFileDialogAsync(It.IsAny<string>())).ReturnsAsync(false);
        var sut = fixture.CreateSut();

        // Act
        await sut.RevertAsync(fixture.Host.Object, tab);

        // Assert
        fixture.Host.Verify(h => h.Reload(It.IsAny<EditorTabItemViewModel>()), Times.Never);
    }

    [Fact]
    public async Task RevertAsync_TabHasUnsavedChangesAndUserAgrees_ReloadsIt()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        var tab = fixture.CreateTab("a", dirty: true);
        fixture.ActiveTab = tab;
        var sut = fixture.CreateSut();

        // Act
        await sut.RevertAsync(fixture.Host.Object, tab);

        // Assert
        fixture.Host.Verify(h => h.Reload(tab), Times.Once);
    }

    [Fact]
    public async Task RevertAsync_FileIsGone_SaysSoAndDoesNotReload()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        var tab = fixture.CreateTab("a", exists: false);
        fixture.ActiveTab = tab;
        var sut = fixture.CreateSut();

        // Act
        await sut.RevertAsync(fixture.Host.Object, tab);

        // Assert
        fixture.Windows.Verify(w => w.ShowErrorAsync("Revert Failed", "'a.inv' no longer exists on disk."), Times.Once);
        fixture.Host.Verify(h => h.Reload(It.IsAny<EditorTabItemViewModel>()), Times.Never);
    }

    [Fact]
    public async Task RevertAsync_AnotherTabWasSelectedWhileAsking_DoesNotReload()
    {
        // Arrange
        var fixture = new TabDiskSyncFixture();
        var tab = fixture.CreateTab("a", dirty: true);
        var other = fixture.CreateTab("b");
        fixture.ActiveTab = tab;
        fixture.Windows.Setup(w => w.ShowRevertFileDialogAsync(It.IsAny<string>()))
            .Returns(() =>
            {
                fixture.ActiveTab = other;
                return Task.FromResult(true);
            });
        var sut = fixture.CreateSut();

        // Act
        await sut.RevertAsync(fixture.Host.Object, tab);

        // Assert
        fixture.Host.Verify(h => h.Reload(It.IsAny<EditorTabItemViewModel>()), Times.Never);
    }
}
