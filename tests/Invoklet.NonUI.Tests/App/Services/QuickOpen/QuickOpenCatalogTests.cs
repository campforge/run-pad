using Invoklet.Services;
using Invoklet.Services.QuickOpen;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services.QuickOpen;

public sealed class QuickOpenCatalogTests
{
    private static readonly string Root = Path.GetFullPath("/scripts");

    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    private static FileSystemItem File(string path) => new(Path.GetFileName(path), false, path, null);

    private static FileSystemItem Dir(string path, params FileSystemItem[] children) => new(Path.GetFileName(path), true, path, children);

    [Fact]
    public void Build_ScriptsInNestedFolders_AreListedWithTheirFolderRelativeToTheRoot()
    {
        // Arrange
        var tree = new[] { File(P("a.inv")), Dir(P("sales"), File(P("sales", "q1.inv"))) };

        // Act
        var entries = QuickOpenCatalog.Build(tree, Root, [], _ => true, StringComparer.Ordinal);

        // Assert
        Assert.Equal("", entries.Single(e => e.Name == "a").Folder);
        Assert.Equal("sales", entries.Single(e => e.Name == "q1").Folder);
    }

    [Fact]
    public void Build_FilesThatAreNotScripts_AreLeftOut()
    {
        // Arrange
        var tree = new[] { File(P("a.inv")), File(P("notes.txt")), File(P("b.linq")) };

        // Act
        var entries = QuickOpenCatalog.Build(tree, Root, [], _ => true, StringComparer.Ordinal);

        // Assert
        Assert.Equal(["a"], entries.Select(e => e.Name));
    }

    [Fact]
    public void Build_RecentFiles_CarryTheirOrder()
    {
        // Arrange
        var tree = new[] { File(P("a.inv")), File(P("b.inv")), File(P("c.inv")) };

        // Act
        var entries = QuickOpenCatalog.Build(tree, Root, [P("c.inv"), P("a.inv")], _ => true, StringComparer.Ordinal);

        // Assert
        Assert.Equal(0, entries.Single(e => e.Name == "c").RecentRank);
        Assert.Equal(1, entries.Single(e => e.Name == "a").RecentRank);
        Assert.Equal(-1, entries.Single(e => e.Name == "b").RecentRank);
    }

    [Fact]
    public void Build_ARecentFileOutsideTheRoot_IsListedWithItsFullFolder()
    {
        // Arrange
        var outside = Path.GetFullPath("/elsewhere/tools/x.inv");

        // Act
        var entries = QuickOpenCatalog.Build([], Root, [outside], _ => true, StringComparer.Ordinal);

        // Assert
        Assert.Equal(Path.GetDirectoryName(outside), Assert.Single(entries).Folder);
    }

    [Fact]
    public void Build_ARecentFileThatIsGone_IsLeftOut()
    {
        // Arrange
        var gone = P("gone.inv");

        // Act
        var entries = QuickOpenCatalog.Build([], Root, [gone], _ => false, StringComparer.Ordinal);

        // Assert
        Assert.Empty(entries);
    }

    [Fact]
    public void Build_ARecentFileAlsoInTheTree_IsListedOnce()
    {
        // Arrange
        var tree = new[] { File(P("a.inv")) };

        // Act
        var entries = QuickOpenCatalog.Build(tree, Root, [P("a.inv")], _ => true, StringComparer.Ordinal);

        // Assert
        Assert.Single(entries);
    }

    [Fact]
    public void Build_ADirectoryWithoutChildrenListed_ContributesNothing()
    {
        // Arrange
        var tree = new[] { new FileSystemItem("empty", true, P("empty"), null) };

        // Act
        var entries = QuickOpenCatalog.Build(tree, Root, [], _ => true, StringComparer.Ordinal);

        // Assert
        Assert.Empty(entries);
    }
}
