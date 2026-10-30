using Invoklet.Services;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class FileTreeOperationsTests
{
    private static string P(params string[] parts) => FileTreeOperationsFixture.P(parts);

    [Fact]
    public async Task DeleteAsync_OpenFile_SavesItBeforeTheDiskThenClosesItAndRemovesTheNode()
    {
        // Arrange
        var fixture = new FileTreeOperationsFixture();
        fixture.OpenTabs(P("a.inv"), P("b.inv"));
        var sut = fixture.CreateSut();

        // Act
        await sut.DeleteAsync(P("a.inv"));

        // Assert
        Assert.Equal(["save", "disk"], fixture.Calls);
        fixture.Disk.Verify(d => d.Delete(P("a.inv"), false), Times.Once);
        fixture.Tabs.Verify(t => t.SaveAsync(It.Is<IReadOnlyList<FileOperationTab>>(p => FileTreeOperationsFixture.Paths(P("a.inv"))(p)), "Delete Failed"), Times.Once);
        fixture.Tabs.Verify(t => t.Close(It.Is<IReadOnlyList<FileOperationTab>>(p => FileTreeOperationsFixture.Paths(P("a.inv"))(p))), Times.Once);
        fixture.Tree.Verify(t => t.Remove(P("a.inv")), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_Folder_ClosesEveryTabBelowIt()
    {
        // Arrange
        var fixture = new FileTreeOperationsFixture();
        fixture.OpenTabs(P("sub", "a.inv"), P("sub", "deep", "b.inv"), P("subtle", "c.inv"));
        fixture.FolderExists(P("sub"));
        var sut = fixture.CreateSut();

        // Act
        await sut.DeleteAsync(P("sub"));

        // Assert
        fixture.Disk.Verify(d => d.Delete(P("sub"), true), Times.Once);
        fixture.Tabs.Verify(t => t.Close(It.Is<IReadOnlyList<FileOperationTab>>(p => FileTreeOperationsFixture.Paths(P("sub", "a.inv"), P("sub", "deep", "b.inv"))(p))), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_ATabCannotBeSaved_LeavesTheFileTheTabsAndTheNodeAlone()
    {
        // Arrange
        var fixture = new FileTreeOperationsFixture();
        fixture.OpenTabs(P("a.inv"));
        fixture.SaveSucceeds(false);
        var sut = fixture.CreateSut();

        // Act
        await sut.DeleteAsync(P("a.inv"));

        // Assert
        fixture.Disk.Verify(d => d.Delete(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        fixture.Tabs.Verify(t => t.Close(It.IsAny<IReadOnlyList<FileOperationTab>>()), Times.Never);
        fixture.Tree.Verify(t => t.Remove(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_DiskRefuses_ShowsTheErrorAndKeepsTheTabsAndTheNode()
    {
        // Arrange
        var fixture = new FileTreeOperationsFixture();
        fixture.OpenTabs(P("a.inv"));
        fixture.Disk.Setup(d => d.Delete(P("a.inv"), false)).Throws(new IOException("in use"));
        var sut = fixture.CreateSut();

        // Act
        await sut.DeleteAsync(P("a.inv"));

        // Assert
        fixture.Windows.Verify(w => w.ShowErrorAsync("Delete Failed", It.Is<string>(m => m.Contains("a.inv") && m.Contains("in use"))), Times.Once);
        fixture.Tabs.Verify(t => t.Close(It.IsAny<IReadOnlyList<FileOperationTab>>()), Times.Never);
        fixture.Tree.Verify(t => t.Remove(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RenameAsync_OpenFile_MovesItRefreshesTheTreeAndReopensTheTabAtTheNewPath()
    {
        // Arrange
        var fixture = new FileTreeOperationsFixture();
        fixture.OpenTabs(P("a.inv"));
        var sut = fixture.CreateSut();

        // Act
        await sut.RenameAsync(P("a.inv"), "b");

        // Assert
        Assert.Equal(["save", "disk"], fixture.Calls);
        fixture.Disk.Verify(d => d.Move(P("a.inv"), P("b.inv"), false), Times.Once);
        fixture.Tree.Verify(t => t.Refresh(), Times.Once);
        fixture.Tabs.Verify(t => t.Close(It.Is<IReadOnlyList<FileOperationTab>>(p => FileTreeOperationsFixture.Paths(P("a.inv"))(p))), Times.Once);
        fixture.Tabs.Verify(t => t.Open(P("b.inv")), Times.Once);
    }

    [Fact]
    public async Task RenameAsync_DiskRefuses_ShowsTheErrorNamingTheTargetAndKeepsTheTabs()
    {
        // Arrange
        var fixture = new FileTreeOperationsFixture();
        fixture.OpenTabs(P("a.inv"));
        fixture.Disk.Setup(d => d.Move(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).Throws(new IOException("exists"));
        var sut = fixture.CreateSut();

        // Act
        await sut.RenameAsync(P("a.inv"), "b");

        // Assert
        fixture.Windows.Verify(w => w.ShowErrorAsync("Rename Failed", It.Is<string>(m => m.Contains("to 'b.inv'") && m.Contains("exists"))), Times.Once);
        fixture.Tabs.Verify(t => t.Close(It.IsAny<IReadOnlyList<FileOperationTab>>()), Times.Never);
        fixture.Tabs.Verify(t => t.Open(It.IsAny<string>()), Times.Never);
        fixture.Tree.Verify(t => t.Refresh(), Times.Never);
    }

    [Fact]
    public async Task MoveAsync_FolderWithOpenTabs_ReopensEachBelowTheNewPlaceOfTheFolder()
    {
        // Arrange
        var fixture = new FileTreeOperationsFixture();
        fixture.OpenTabs(P("a", "x.inv"), P("a", "deep", "y.inv"));
        fixture.FolderExists(P("a"));
        fixture.FolderExists(P("b"));
        var sut = fixture.CreateSut();

        // Act
        await sut.MoveAsync(P("a"), P("b"));

        // Assert
        fixture.Disk.Verify(d => d.Move(P("a"), P("b", "a"), true), Times.Once);
        fixture.Tabs.Verify(t => t.Open(P("b", "a", "x.inv")), Times.Once);
        fixture.Tabs.Verify(t => t.Open(P("b", "a", "deep", "y.inv")), Times.Once);
    }

    [Fact]
    public async Task MoveAsync_DroppedOnAFile_MovesAlongsideItWithoutAskingForTheFolder()
    {
        // Arrange
        var fixture = new FileTreeOperationsFixture();
        var sut = fixture.CreateSut();

        // Act
        await sut.MoveAsync(P("a", "x.inv"), P("b", "target.inv"));

        // Assert
        fixture.Disk.Verify(d => d.Move(P("a", "x.inv"), P("b", "x.inv"), false), Times.Once);
    }

    [Fact]
    public async Task MoveAsync_DroppedWhereItAlreadyIs_TouchesNothing()
    {
        // Arrange
        var fixture = new FileTreeOperationsFixture();
        var sut = fixture.CreateSut();

        // Act
        await sut.MoveAsync(P("x.inv"), null);

        // Assert
        Assert.Empty(fixture.Calls);
        fixture.Tabs.Verify(t => t.SaveAsync(It.IsAny<IReadOnlyList<FileOperationTab>>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MoveAsync_ATabCannotBeSaved_StopsBeforeTheDisk()
    {
        // Arrange
        var fixture = new FileTreeOperationsFixture();
        fixture.OpenTabs(P("a", "x.inv"));
        fixture.SaveSucceeds(false);
        var sut = fixture.CreateSut();

        // Act
        await sut.MoveAsync(P("a", "x.inv"), null);

        // Assert
        Assert.Equal(["save"], fixture.Calls);
        fixture.Tabs.Verify(t => t.Open(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MoveAsync_SaveAsChangesPath_ClosesNoTabAndOpensNoDuplicate()
    {
        // Arrange: the moved file is open and edited; asked to save it, the user saves it under another name
        var fixture = new FileTreeOperationsFixture();
        fixture.OpenTabs(P("a", "x.inv"));
        fixture.FolderExists(P("b"));
        fixture.SaveAsMovesTab(P("a", "x.inv"), P("elsewhere", "copy.inv"));
        var sut = fixture.CreateSut();

        // Act
        await sut.MoveAsync(P("a", "x.inv"), P("b"));

        // Assert: the file moved, but the tab is on another file now and stays as it is
        fixture.Disk.Verify(d => d.Move(P("a", "x.inv"), P("b", "x.inv"), false), Times.Once);
        fixture.Tabs.Verify(t => t.Close(It.Is<IReadOnlyList<FileOperationTab>>(p => p.Count == 0)), Times.Once);
        fixture.Tabs.Verify(t => t.Open(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MoveAsync_SaveAsKeepsTheTabInTheFolder_ReopensItAtItsCurrentPlaceBelowTheNewFolder()
    {
        // Arrange: a tab of a moved folder is saved under another name inside the same folder
        var fixture = new FileTreeOperationsFixture();
        fixture.OpenTabs(P("a", "x.inv"));
        fixture.FolderExists(P("a"));
        fixture.FolderExists(P("b"));
        fixture.SaveAsMovesTab(P("a", "x.inv"), P("a", "renamed.inv"));
        var sut = fixture.CreateSut();

        // Act
        await sut.MoveAsync(P("a"), P("b"));

        // Assert
        fixture.Tabs.Verify(t => t.Open(P("b", "a", "renamed.inv")), Times.Once);
        fixture.Tabs.Verify(t => t.Open(P("b", "a", "x.inv")), Times.Never);
    }

    [Fact]
    public async Task RenameAsync_SaveAsChangesPath_ClosesNoTabAndOpensNoDuplicate()
    {
        // Arrange
        var fixture = new FileTreeOperationsFixture();
        fixture.OpenTabs(P("a.inv"));
        fixture.SaveAsMovesTab(P("a.inv"), P("other", "copy.inv"));
        var sut = fixture.CreateSut();

        // Act
        await sut.RenameAsync(P("a.inv"), "b");

        // Assert
        fixture.Disk.Verify(d => d.Move(P("a.inv"), P("b.inv"), false), Times.Once);
        fixture.Tabs.Verify(t => t.Close(It.Is<IReadOnlyList<FileOperationTab>>(p => p.Count == 0)), Times.Once);
        fixture.Tabs.Verify(t => t.Open(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_SaveAsChangesPath_KeepsTheTabOfTheNewFile()
    {
        // Arrange
        var fixture = new FileTreeOperationsFixture();
        fixture.OpenTabs(P("a.inv"));
        fixture.SaveAsMovesTab(P("a.inv"), P("other", "copy.inv"));
        var sut = fixture.CreateSut();

        // Act
        await sut.DeleteAsync(P("a.inv"));

        // Assert
        fixture.Disk.Verify(d => d.Delete(P("a.inv"), false), Times.Once);
        fixture.Tabs.Verify(t => t.Close(It.Is<IReadOnlyList<FileOperationTab>>(p => p.Count == 0)), Times.Once);
        fixture.Tree.Verify(t => t.Remove(P("a.inv")), Times.Once);
    }

    [Fact]
    public async Task MoveAsync_TabClosedWhileTheSaveIsDecided_IsNotClosedAgainNorReopened()
    {
        // Arrange
        var fixture = new FileTreeOperationsFixture();
        fixture.OpenTabs(P("a", "x.inv"));
        fixture.ClosedWhileSaving(P("a", "x.inv"));
        var sut = fixture.CreateSut();

        // Act
        await sut.MoveAsync(P("a", "x.inv"), P("b"));

        // Assert
        fixture.Tabs.Verify(t => t.Close(It.Is<IReadOnlyList<FileOperationTab>>(p => p.Count == 0)), Times.Once);
        fixture.Tabs.Verify(t => t.Open(It.IsAny<string>()), Times.Never);
    }
}
