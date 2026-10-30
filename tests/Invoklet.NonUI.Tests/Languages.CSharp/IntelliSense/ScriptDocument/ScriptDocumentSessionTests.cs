using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class ScriptDocumentSessionTests
{
    private static async Task<string> TextOfAsync(Document document)
        => (await document.GetTextAsync(TestContext.Current.CancellationToken)).ToString();

    [Fact]
    public async Task Sync_FirstCode_PutsTheTextOfTheLayoutInTheDocument()
    {
        // Arrange
        var fixture = new ScriptDocumentSessionFixture();
        using var sut = fixture.CreateSut();

        // Act
        var document = sut.Sync("var a = 1;");

        // Assert
        Assert.Equal(fixture.Layout.FullText("var a = 1;"), await TextOfAsync(document));
    }

    [Fact]
    public async Task Sync_TheSameCodeAgain_LeavesTheTextAndItsLineageAsTheyWere()
    {
        // Arrange
        var fixture = new ScriptDocumentSessionFixture();
        using var sut = fixture.CreateSut();
        sut.Sync("var a = 1;");
        var before = sut.LastSyncedSourceText;

        // Act
        var document = sut.Sync("var a = 1;");

        // Assert
        Assert.Same(before, sut.LastSyncedSourceText);
        Assert.Equal(fixture.Layout.FullText("var a = 1;"), await TextOfAsync(document));
    }

    [Fact]
    public void Sync_AnEditedCode_KeepsTheLineageOfTheSourceTextSoRoslynCanReuseWhatItParsed()
    {
        // Arrange
        var fixture = new ScriptDocumentSessionFixture();
        using var sut = fixture.CreateSut();
        sut.Sync("var a = 1;");
        var older = sut.LastSyncedSourceText!;

        // Act
        sut.Sync("var a = 12;");
        var ranges = sut.LastSyncedSourceText!.GetChangeRanges(older);

        // Assert
        Assert.NotEmpty(ranges);
    }

    [Fact]
    public void InvalidateBaseline_Then_Sync_BuildsTheWholeTextAgainInsteadOfKeepingTheOldOne()
    {
        // Arrange
        var fixture = new ScriptDocumentSessionFixture();
        using var sut = fixture.CreateSut();
        sut.Sync("var a = 1;");
        var older = sut.LastSyncedSourceText!;

        // Act
        sut.InvalidateBaseline();
        sut.Sync("var a = 1;");

        // Assert: the same code, and yet a text of its own, with nothing of the old lineage
        Assert.NotSame(older, sut.LastSyncedSourceText);
        Assert.Equal(older.ToString(), sut.LastSyncedSourceText!.ToString());
    }

    [Fact]
    public void ContextChanged_CountsTheChangeAndMakesTheEarlierVersionStale()
    {
        // Arrange
        var fixture = new ScriptDocumentSessionFixture();
        using var sut = fixture.CreateSut();
        var before = sut.ContextVersion;

        // Act
        sut.ContextChanged();

        // Assert
        Assert.False(sut.IsCurrent(before));
        Assert.True(sut.IsCurrent(sut.ContextVersion));
        Assert.Equal(before + 1, sut.ContextVersion);
    }

    [Fact]
    public void ContextChanged_Then_Sync_StillDiffsAgainstTheBaselineBecauseTheTextDidNotChange()
    {
        // Arrange
        var fixture = new ScriptDocumentSessionFixture();
        using var sut = fixture.CreateSut();
        sut.Sync("var a = 1;");
        var older = sut.LastSyncedSourceText!;

        // Act
        sut.ContextChanged();
        sut.Sync("var a = 12;");

        // Assert
        Assert.NotEmpty(sut.LastSyncedSourceText!.GetChangeRanges(older));
    }

    [Fact]
    public void UpdateIndentation_TabsAndAWidth_AreInTheFormattingOptions()
    {
        // Arrange
        var fixture = new ScriptDocumentSessionFixture();
        using var sut = fixture.CreateSut();

        // Act
        sut.UpdateIndentation(useTabs: true, indentationSize: 2);
        var options = sut.FormattingOptionsSnapshot();

        // Assert
        Assert.True(options.GetOption(FormattingOptions.UseTabs, LanguageNames.CSharp));
        Assert.Equal(2, options.GetOption(FormattingOptions.IndentationSize, LanguageNames.CSharp));
        Assert.Equal(2, options.GetOption(FormattingOptions.TabSize, LanguageNames.CSharp));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(100, 16)]
    public void UpdateIndentation_AWidthOutsideTheRange_IsClamped(int width, int expected)
    {
        // Arrange
        var fixture = new ScriptDocumentSessionFixture();
        using var sut = fixture.CreateSut();

        // Act
        sut.UpdateIndentation(useTabs: false, indentationSize: width);

        // Assert
        Assert.Equal(expected, sut.FormattingOptionsSnapshot().GetOption(FormattingOptions.IndentationSize, LanguageNames.CSharp));
    }

    [Fact]
    public void Workspace_New_FormatsWithALineFeedWhateverTheOperatingSystem()
    {
        // Arrange
        var fixture = new ScriptDocumentSessionFixture();
        using var sut = fixture.CreateSut();

        // Act
        var newLine = sut.Workspace.CurrentSolution.Options.GetOption(FormattingOptions.NewLine, LanguageNames.CSharp);

        // Assert
        Assert.Equal("\n", newLine);
    }
}
