using Invoklet.NonUI.Tests.App.Services.Fixtures;
using Invoklet.Services;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class UnsavedChangesFlowTests
{
    [Fact]
    public async Task ResolveBeforeCloseAsync_CleanTab_MayCloseWithoutAskingAnything()
    {
        // Arrange
        var fixture = new UnsavedChangesFlowFixture();
        var tab = fixture.CreateTab("/a.inv", dirty: false);
        var sut = fixture.CreateSut();

        // Act
        var mayClose = await sut.ResolveBeforeCloseAsync(tab);

        // Assert
        Assert.True(mayClose);
        Assert.Empty(fixture.Calls);
        fixture.Windows.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolveBeforeCloseAsync_NamedTabSaves_MayCloseWithoutAQuestion()
    {
        // Arrange
        var fixture = new UnsavedChangesFlowFixture();
        var tab = fixture.CreateTab("/a.inv", dirty: true);
        var sut = fixture.CreateSut();

        // Act
        var mayClose = await sut.ResolveBeforeCloseAsync(tab);

        // Assert
        Assert.True(mayClose);
        fixture.Windows.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ResolveBeforeCloseAsync_SaveOfNamedTabDeclined_KeepsTheTabOpenWithoutAsking()
    {
        // Arrange
        var fixture = new UnsavedChangesFlowFixture();
        var tab = fixture.CreateTab("/a.inv", dirty: true);
        fixture.SaveResults[tab] = InPlaceSaveResult.Cancelled;
        var sut = fixture.CreateSut();

        // Act
        var mayClose = await sut.ResolveBeforeCloseAsync(tab);

        // Assert
        Assert.False(mayClose);
        fixture.Windows.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(UnsavedChangesResult.Cancel, false)]
    [InlineData(UnsavedChangesResult.DontSave, true)]
    public async Task ResolveBeforeCloseAsync_SaveOfNamedTabFails_ShowsTheErrorThenDoesWhatTheUserChooses(UnsavedChangesResult choice, bool mayClose)
    {
        // Arrange
        var fixture = new UnsavedChangesFlowFixture();
        var tab = fixture.CreateTab("/a.inv", dirty: true);
        fixture.SaveResults[tab] = new InPlaceSaveResult(InPlaceSaveStatus.Failed, "read-only");
        fixture.UserChooses(choice);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.ResolveBeforeCloseAsync(tab);

        // Assert
        Assert.Equal(mayClose, result);
        fixture.Windows.Verify(w => w.ShowErrorAsync("Save Failed", "read-only"), Times.Once);
    }

    [Fact]
    public async Task ResolveBeforeCloseAsync_UntitledTabAndTheUserSaves_MayCloseOnceSaveAsWrote()
    {
        // Arrange
        var fixture = new UnsavedChangesFlowFixture();
        var tab = fixture.CreateTab(null, dirty: true);
        fixture.UserChooses(UnsavedChangesResult.Save);
        var sut = fixture.CreateSut();

        // Act
        var mayClose = await sut.ResolveBeforeCloseAsync(tab);

        // Assert
        Assert.True(mayClose);
        Assert.Equal(["saveAs Untitled1"], fixture.Calls);
    }

    [Fact]
    public async Task ResolveBeforeCloseAsync_UntitledTabAndSaveAsIsCancelled_KeepsTheTabOpen()
    {
        // Arrange
        var fixture = new UnsavedChangesFlowFixture { SaveAsWrites = false };
        var tab = fixture.CreateTab(null, dirty: true);
        fixture.UserChooses(UnsavedChangesResult.Save);
        var sut = fixture.CreateSut();

        // Act
        var mayClose = await sut.ResolveBeforeCloseAsync(tab);

        // Assert
        Assert.False(mayClose);
    }

    [Fact]
    public async Task SaveAllAsync_NamedAndUntitledTabs_SavesTheFirstInPlaceAndTheSecondThroughSaveAs()
    {
        // Arrange
        var fixture = new UnsavedChangesFlowFixture();
        var named = fixture.CreateTab("/a.inv", dirty: true);
        var untitled = fixture.CreateTab(null, dirty: true);
        var sut = fixture.CreateSut();

        // Act
        var saved = await sut.SaveAllAsync([named, untitled], _ => true);

        // Assert
        Assert.True(saved);
        Assert.Equal(["save " + named.FileName, "saveAs " + untitled.FileName], fixture.Calls);
    }

    [Fact]
    public async Task SaveAllAsync_ATabIsCleanOrClosedMeanwhile_IsSkipped()
    {
        // Arrange
        var fixture = new UnsavedChangesFlowFixture();
        var clean = fixture.CreateTab("/a.inv", dirty: false);
        var closed = fixture.CreateTab("/b.inv", dirty: true);
        var sut = fixture.CreateSut();

        // Act
        var saved = await sut.SaveAllAsync([clean, closed], tab => tab != closed);

        // Assert
        Assert.True(saved);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task SaveAllAsync_TheUserDeclinesOneSave_StopsThere()
    {
        // Arrange
        var fixture = new UnsavedChangesFlowFixture();
        var first = fixture.CreateTab("/a.inv", dirty: true);
        var second = fixture.CreateTab("/b.inv", dirty: true);
        fixture.SaveResults[first] = InPlaceSaveResult.Cancelled;
        var sut = fixture.CreateSut();

        // Act
        var saved = await sut.SaveAllAsync([first, second], _ => true);

        // Assert
        Assert.False(saved);
        Assert.Single(fixture.Calls);
    }

    [Fact]
    public async Task SaveAllAsync_ASaveFails_ShowsTheErrorAndStops()
    {
        // Arrange
        var fixture = new UnsavedChangesFlowFixture();
        var first = fixture.CreateTab("/a.inv", dirty: true);
        var second = fixture.CreateTab("/b.inv", dirty: true);
        fixture.SaveResults[first] = new InPlaceSaveResult(InPlaceSaveStatus.Failed, "disk full");
        var sut = fixture.CreateSut();

        // Act
        var saved = await sut.SaveAllAsync([first, second], _ => true);

        // Assert
        Assert.False(saved);
        fixture.Windows.Verify(w => w.ShowErrorAsync("Save Failed", "disk full"), Times.Once);
        Assert.Single(fixture.Calls);
    }

    [Fact]
    public async Task SaveAllAsync_SaveAsOfAnUntitledTabIsCancelled_StopsAndKeepsTheSavesAlreadyMade()
    {
        // Arrange
        var fixture = new UnsavedChangesFlowFixture { SaveAsWrites = false };
        var named = fixture.CreateTab("/a.inv", dirty: true);
        var untitled = fixture.CreateTab(null, dirty: true);
        var after = fixture.CreateTab("/c.inv", dirty: true);
        var sut = fixture.CreateSut();

        // Act
        var saved = await sut.SaveAllAsync([named, untitled, after], _ => true);

        // Assert
        Assert.False(saved);
        Assert.Equal(["save " + named.FileName, "saveAs " + untitled.FileName], fixture.Calls);
    }

    [Fact]
    public async Task SaveBeforeFileOperationAsync_AllTabsSave_IsTrue()
    {
        // Arrange
        var fixture = new UnsavedChangesFlowFixture();
        var tabs = new[] { fixture.CreateTab("/a.inv", dirty: true), fixture.CreateTab("/b.inv", dirty: true) };
        var sut = fixture.CreateSut();

        // Act
        var saved = await sut.SaveBeforeFileOperationAsync(tabs, "Delete Failed");

        // Assert
        Assert.True(saved);
        Assert.Equal(2, fixture.Calls.Count);
    }

    [Fact]
    public async Task SaveBeforeFileOperationAsync_ASaveFails_ShowsTheErrorUnderTheTitleOfTheOperationAndStops()
    {
        // Arrange
        var fixture = new UnsavedChangesFlowFixture();
        var first = fixture.CreateTab("/a.inv", dirty: true);
        var second = fixture.CreateTab("/b.inv", dirty: true);
        fixture.SaveResults[first] = new InPlaceSaveResult(InPlaceSaveStatus.Failed, "locked");
        var sut = fixture.CreateSut();

        // Act
        var saved = await sut.SaveBeforeFileOperationAsync([first, second], "Move Failed");

        // Assert
        Assert.False(saved);
        fixture.Windows.Verify(w => w.ShowErrorAsync("Move Failed", "locked"), Times.Once);
        Assert.Single(fixture.Calls);
    }

    [Fact]
    public async Task SaveBeforeFileOperationAsync_TheUserDeclinesToReplaceAChangedFile_StopsWithoutAMessage()
    {
        // Arrange
        var fixture = new UnsavedChangesFlowFixture();
        var tab = fixture.CreateTab("/a.inv", dirty: true);
        fixture.SaveResults[tab] = InPlaceSaveResult.Cancelled;
        var sut = fixture.CreateSut();

        // Act
        var saved = await sut.SaveBeforeFileOperationAsync([tab], "Delete Failed");

        // Assert
        Assert.False(saved);
        fixture.Windows.Verify(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
