using Invoklet.Format.MdRun;
using Invoklet.Services;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class DependencyDraftTests
{
    [Fact]
    public void Load_FrontMatterWithPackages_ListsThemInOrder()
    {
        // Arrange
        var frontMatter = DependencyDraftFixture.CreateFrontMatter(("Dapper", "2.1.0"), ("Serilog", "4.0.0"));
        var sut = new DependencyDraftFixture().CreateSut();

        // Act
        sut.Load(frontMatter);

        // Assert
        Assert.Equal(["Dapper 2.1.0", "Serilog 4.0.0"], sut.Packages.Select(p => $"{p.Id} {p.Version}"));
    }

    [Fact]
    public void Load_Null_EmptiesTheDraft()
    {
        // Arrange
        var sut = new DependencyDraftFixture().CreateSut(DependencyDraftFixture.CreateFrontMatter(("Dapper", "2.1.0")));

        // Act
        sut.Load(null);

        // Assert
        Assert.Empty(sut.Packages);
    }

    [Fact]
    public void Add_NewPackage_IsAddedWithATrimmedVersion()
    {
        // Arrange
        var sut = new DependencyDraftFixture().CreateSut();

        // Act
        var added = sut.Add("Dapper", " 2.1.0 ");

        // Assert
        Assert.True(added);
        Assert.Equal("2.1.0", sut.Find("Dapper")!.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_BlankVersion_IsRefused(string? version)
    {
        // Arrange
        var sut = new DependencyDraftFixture().CreateSut();

        // Act
        var added = sut.Add("Dapper", version);

        // Assert
        Assert.False(added);
        Assert.Empty(sut.Packages);
    }

    [Fact]
    public void Add_PackageAlreadyThereUnderAnotherCase_IsRefusedAndTheOldEntryKept()
    {
        // Arrange
        var sut = new DependencyDraftFixture().CreateSut(DependencyDraftFixture.CreateFrontMatter(("Dapper", "2.1.0")));

        // Act
        var added = sut.Add("DAPPER", "3.0.0");

        // Assert
        Assert.False(added);
        Assert.Equal("2.1.0", sut.Find("dapper")!.Version);
    }

    [Fact]
    public void Update_ExistingPackage_GivesItTheTrimmedVersion()
    {
        // Arrange
        var sut = new DependencyDraftFixture().CreateSut(DependencyDraftFixture.CreateFrontMatter(("Dapper", "2.1.0")));

        // Act
        var updated = sut.Update("dapper", " 3.0.0 ");

        // Assert
        Assert.True(updated);
        Assert.Equal("3.0.0", sut.Find("Dapper")!.Version);
    }

    [Fact]
    public void Update_PackageNotThereOrBlankVersion_ChangesNothing()
    {
        // Arrange
        var sut = new DependencyDraftFixture().CreateSut(DependencyDraftFixture.CreateFrontMatter(("Dapper", "2.1.0")));

        // Act
        var missing = sut.Update("Serilog", "4.0.0");
        var blank = sut.Update("Dapper", " ");

        // Assert
        Assert.False(missing);
        Assert.False(blank);
        Assert.Equal("2.1.0", sut.Find("Dapper")!.Version);
    }

    [Fact]
    public void Remove_ExistingPackage_RemovesIt()
    {
        // Arrange
        var sut = new DependencyDraftFixture().CreateSut(DependencyDraftFixture.CreateFrontMatter(("Dapper", "2.1.0")));

        // Act
        var removed = sut.Remove("DAPPER");

        // Assert
        Assert.True(removed);
        Assert.Empty(sut.Packages);
        Assert.False(sut.Remove("Dapper"));
    }

    [Fact]
    public void DiffersFrom_SamePackagesInTheSameOrder_IsFalse()
    {
        // Arrange
        var frontMatter = DependencyDraftFixture.CreateFrontMatter(("A", "1"), ("B", "2"));
        var sut = new DependencyDraftFixture().CreateSut(frontMatter);

        // Act
        var differs = sut.DiffersFrom(frontMatter);

        // Assert
        Assert.False(differs);
    }

    [Fact]
    public void DiffersFrom_AVersionAnAdditionOrARemoval_IsTrue()
    {
        // Arrange
        var frontMatter = DependencyDraftFixture.CreateFrontMatter(("A", "1"), ("B", "2"));
        var changedVersion = new DependencyDraftFixture().CreateSut(frontMatter);
        changedVersion.Update("A", "9");
        var added = new DependencyDraftFixture().CreateSut(frontMatter);
        added.Add("C", "3");
        var removed = new DependencyDraftFixture().CreateSut(frontMatter);
        removed.Remove("B");

        // Act + Assert
        Assert.True(changedVersion.DiffersFrom(frontMatter));
        Assert.True(added.DiffersFrom(frontMatter));
        Assert.True(removed.DiffersFrom(frontMatter));
    }

    [Fact]
    public void DiffersFrom_FrontMatterWithoutPackagesAndAnEmptyDraft_IsFalse()
    {
        // Arrange
        var sut = new DependencyDraftFixture().CreateSut();

        // Act
        var differs = sut.DiffersFrom(new MdRunFrontMatter());

        // Assert
        Assert.False(differs);
    }

    [Fact]
    public void ApplyTo_Draft_ReplacesThePackagesAndKeepsTheObjectsOfUnchangedOnes()
    {
        // Arrange
        var frontMatter = DependencyDraftFixture.CreateFrontMatter(("A", "1"), ("B", "2"), ("C", "3"));
        var untouched = frontMatter.Packages!["A"];
        var sut = new DependencyDraftFixture().CreateSut(frontMatter);
        sut.Update("B", "20");
        sut.Remove("C");
        sut.Add("D", "4");

        // Act
        sut.ApplyTo(frontMatter);

        // Assert
        Assert.Equal(["A", "B", "D"], frontMatter.Packages!.Keys);
        Assert.Same(untouched, frontMatter.Packages["A"]);
        Assert.Equal("20", frontMatter.Packages["B"].Version);
        Assert.Equal("4", frontMatter.Packages["D"].Version);
    }

    [Fact]
    public void ApplyTo_FrontMatterWithoutPackages_CreatesThem()
    {
        // Arrange
        var frontMatter = new MdRunFrontMatter();
        var sut = new DependencyDraftFixture().CreateSut();
        sut.Add("A", "1");

        // Act
        sut.ApplyTo(frontMatter);

        // Assert
        Assert.Equal("1", frontMatter.Packages!["A"].Version);
    }

    [Fact]
    public void CopyBack_OriginalNullAndNothingDrafted_StaysNull()
    {
        // Arrange (no original list, no values)

        // Act
        var result = DependencyDraft.CopyBack(null, []);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void CopyBack_OriginalList_KeepsTheSameObjectWithTheDraftsValues()
    {
        // Arrange
        List<string> original = ["old"];

        // Act
        var result = DependencyDraft.CopyBack(original, ["a", "b"]);

        // Assert
        Assert.Same(original, result);
        Assert.Equal(["a", "b"], original);
    }

    [Fact]
    public void CopyBack_OriginalNullAndSomeValues_CreatesTheList()
    {
        // Arrange (no original list)

        // Act
        var result = DependencyDraft.CopyBack(null, ["a"]);

        // Assert
        Assert.Equal(["a"], result);
    }
}
