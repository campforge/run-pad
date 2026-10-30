using Invoklet.NonUI.Tests.App.Services.Fixtures;
using Invoklet.Services;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class FileTreePathsTests
{
    private static string P(params string[] parts) => FileTreePathsFixture.P(parts);

    [Fact]
    public void ParentFolder_NothingSelected_IsTheRootOfTheTree()
    {
        // Arrange
        var sut = new FileTreePathsFixture().CreateSut();

        // Act
        var parent = sut.ParentFolder(null, contextIsFolder: false);

        // Assert
        Assert.Equal(FileTreePathsFixture.Root, parent);
    }

    [Fact]
    public void ParentFolder_AFolderIsSelected_IsThatFolder()
    {
        // Arrange
        var sut = new FileTreePathsFixture().CreateSut();

        // Act
        var parent = sut.ParentFolder(P("src"), contextIsFolder: true);

        // Assert
        Assert.Equal(P("src"), parent);
    }

    [Fact]
    public void ParentFolder_AFileIsSelected_IsTheFolderItIsIn()
    {
        // Arrange
        var sut = new FileTreePathsFixture().CreateSut();

        // Act
        var parent = sut.ParentFolder(P("src", "a.inv"), contextIsFolder: false);

        // Assert
        Assert.Equal(P("src"), parent);
    }

    [Theory]
    [InlineData("a.inv", "b", false, "b.inv")]
    [InlineData("a", "b", true, "b")]
    [InlineData("a.v2", "b", true, "b")]
    public void Renamed_FileOrFolder_KeepsTheExtensionOfAFileOnly(string name, string newName, bool isFolder, string expected)
    {
        // Arrange
        var path = P("src", name);

        // Act
        var renamed = FileTreePaths.Renamed(path, newName, isFolder);

        // Assert
        Assert.Equal(P("src", expected), renamed);
    }

    [Fact]
    public void Moved_IntoAnotherFolder_KeepsTheName()
    {
        // Arrange
        var path = P("a", "x.inv");

        // Act
        var moved = FileTreePaths.Moved(path, P("b"));

        // Assert
        Assert.Equal(P("b", "x.inv"), moved);
    }

    [Fact]
    public void Moved_AFolderWithATrailingSeparator_KeepsItsName()
    {
        // Arrange
        var path = P("a", "sub") + Path.DirectorySeparatorChar;

        // Act
        var moved = FileTreePaths.Moved(path, P("b"));

        // Assert
        Assert.Equal(P("b", "sub"), moved);
    }

    [Fact]
    public void IsSamePlace_DroppedWhereItAlreadyIs_IsTrue()
    {
        // Arrange
        var path = P("a", "x.inv");

        // Act
        var same = FileTreePaths.IsSamePlace(FileTreePaths.Moved(path, P("a")), path);

        // Assert
        Assert.True(same);
    }

    [Fact]
    public void AffectedPaths_ForAFile_IsOnlyThatFile()
    {
        // Arrange
        var sut = new FileTreePathsFixture().CreateSut();
        string[] open = [P("a", "x.inv"), P("a", "xx.inv"), P("a", "x.inv.bak")];

        // Act
        var affected = sut.AffectedPaths(open, P("a", "x.inv"), isFolder: false);

        // Assert
        Assert.Equal([P("a", "x.inv")], affected);
    }

    [Fact]
    public void AffectedPaths_ForAFolder_IsEverythingBelowItAndNotASiblingWithTheSamePrefix()
    {
        // Arrange
        var sut = new FileTreePathsFixture().CreateSut();
        string[] open = [P("a", "x.inv"), P("a", "deep", "y.inv"), P("ab", "z.inv"), P("b", "w.inv")];

        // Act
        var affected = sut.AffectedPaths(open, P("a"), isFolder: true);

        // Assert
        Assert.Equal([P("a", "x.inv"), P("a", "deep", "y.inv")], affected);
    }

    [Fact]
    public void AffectedPaths_CaseInsensitiveComparison_MatchesOtherCasing()
    {
        // Arrange
        var sut = new FileTreePathsFixture().CreateSut(StringComparison.OrdinalIgnoreCase);

        // Act
        var affected = sut.AffectedPaths([P("A", "X.INV")], P("a"), isFolder: true);

        // Assert
        Assert.Single(affected);
    }

    [Fact]
    public void Reopened_AFileInAMovedFolder_KeepsItsPlaceBelowTheFolder()
    {
        // Arrange
        var source = P("a");
        var destination = P("b", "a");

        // Act
        var reopened = FileTreePaths.Reopened(source, destination, P("a", "deep", "y.inv"), isFolder: true);

        // Assert
        Assert.Equal(P("b", "a", "deep", "y.inv"), reopened);
    }

    [Fact]
    public void Reopened_ARenamedFile_IsTheNewPath()
    {
        // Arrange
        var destination = P("a", "new.inv");

        // Act
        var reopened = FileTreePaths.Reopened(P("a", "old.inv"), destination, P("a", "old.inv"), isFolder: false);

        // Assert
        Assert.Equal(destination, reopened);
    }
}
