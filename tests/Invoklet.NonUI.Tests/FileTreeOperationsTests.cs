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
        fixture.Tabs.Verify(t => t.SaveAsync(It.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { P("a.inv") })), "Delete Failed"), Times.Once);
        fixture.Tabs.Verify(t => t.Close(It.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { P("a.inv") }))), Times.Once);
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
        fixture.Tabs.Verify(t => t.Close(It.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { P("sub", "a.inv"), P("sub", "deep", "b.inv") }))), Times.Once);
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
        fixture.Tabs.Verify(t => t.Close(It.IsAny<IReadOnlyList<string>>()), Times.Never);
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
        fixture.Tabs.Verify(t => t.Close(It.IsAny<IReadOnlyList<string>>()), Times.Never);
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
        fixture.Tabs.Verify(t => t.Close(It.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { P("a.inv") }))), Times.Once);
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
        fixture.Tabs.Verify(t => t.Close(It.IsAny<IReadOnlyList<string>>()), Times.Never);
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
        fixture.Tabs.Verify(t => t.SaveAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>()), Times.Never);
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
}
