using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Contracts.IntelliSense;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class LightbulbEntriesTests
{
    private static readonly string[] None = [];

    [Fact]
    public void Visible_BothSourcesHaveEntries_ListsTheFixesBeforeTheRefactorings()
    {
        // Arrange
        var fixture = new LightbulbEntriesFixture();
        var sut = fixture.CreateSut();
        sut.SetDiagnosticFixes([LightbulbEntriesFixture.CreateFix("add using", "CS0246")]);
        sut.SetRefactorings([LightbulbEntriesFixture.CreateFix("extract method")]);

        // Act
        var visible = sut.Visible(true, None);

        // Assert
        Assert.Equal(["add using", "extract method"], visible.Select(e => e.Title));
    }

    [Fact]
    public void Visible_CodeFixesSwitchedOff_ListsNothing()
    {
        // Arrange
        var fixture = new LightbulbEntriesFixture();
        var sut = fixture.CreateSut();
        sut.SetDiagnosticFixes([LightbulbEntriesFixture.CreateFix("add using", "CS0246")]);
        sut.SetRefactorings([LightbulbEntriesFixture.CreateFix("extract method")]);

        // Act
        var visible = sut.Visible(false, None);

        // Assert
        Assert.Empty(visible);
    }

    [Fact]
    public void Visible_ExcludedDiagnostic_IsDroppedWhateverItsCase()
    {
        // Arrange
        var fixture = new LightbulbEntriesFixture();
        var sut = fixture.CreateSut();
        sut.SetDiagnosticFixes([LightbulbEntriesFixture.CreateFix("format", "IDE0055"), LightbulbEntriesFixture.CreateFix("add using", "CS0246")]);

        // Act
        var visible = sut.Visible(true, ["ide0055"]);

        // Assert
        Assert.Equal(["add using"], visible.Select(e => e.Title));
    }

    [Fact]
    public void Visible_EntryWithoutADiagnostic_IsNeverExcluded()
    {
        // Arrange
        var fixture = new LightbulbEntriesFixture();
        var sut = fixture.CreateSut();
        sut.SetRefactorings([LightbulbEntriesFixture.CreateFix("extract method", diagnosticId: null)]);

        // Act
        var visible = sut.Visible(true, ["IDE0055", "CS0246"]);

        // Assert
        Assert.Single(visible);
    }

    [Fact]
    public void Visible_PreferencesChangeWithoutNewResults_ReappliesThemToTheKeptLists()
    {
        // Arrange
        var fixture = new LightbulbEntriesFixture();
        var sut = fixture.CreateSut();
        sut.SetDiagnosticFixes([LightbulbEntriesFixture.CreateFix("format", "IDE0055")]);
        var beforeExcluding = sut.Visible(true, None);

        // Act
        var afterExcluding = sut.Visible(true, ["IDE0055"]);

        // Assert
        Assert.Single(beforeExcluding);
        Assert.Empty(afterExcluding);
    }

    [Fact]
    public void SetRefactorings_Null_ClearsThePreviousOnesAndKeepsTheDiagnosticFixes()
    {
        // Arrange
        var fixture = new LightbulbEntriesFixture();
        var sut = fixture.CreateSut();
        sut.SetDiagnosticFixes([LightbulbEntriesFixture.CreateFix("add using", "CS0246")]);
        sut.SetRefactorings([LightbulbEntriesFixture.CreateFix("extract method")]);

        // Act
        sut.SetRefactorings(null);

        // Assert
        Assert.Equal(["add using"], sut.Visible(true, None).Select(e => e.Title));
    }

    [Theory]
    [InlineData(DiagnosticSeverity.Error, CodeFixSeverity.Error)]
    [InlineData(DiagnosticSeverity.Warning, CodeFixSeverity.Warning)]
    [InlineData(DiagnosticSeverity.Info, CodeFixSeverity.Info)]
    public void Visible_FixOfASeverity_CarriesItToTheMarginWithLineAndKey(DiagnosticSeverity severity, CodeFixSeverity expected)
    {
        // Arrange
        var fixture = new LightbulbEntriesFixture();
        var sut = fixture.CreateSut();
        sut.SetDiagnosticFixes([LightbulbEntriesFixture.CreateFix("fix", "X1", severity, line: 7)]);

        // Act
        var entry = sut.Visible(true, None).Single();

        // Assert
        Assert.Equal(new CodeFixEntry(7, "fix", expected, "key-fix", "X1"), entry);
    }
}
