using Invoklet.NonUI.Tests.App.Services.Fixtures;
using Invoklet.Services;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class ResultsExporterTests
{
    [Fact]
    public async Task ExportAsync_ResultsOfASavedScript_AreOfferedUnderItsNameInItsFolderAndWrittenAsAPage()
    {
        // Arrange
        var fixture = new ResultsExporterFixture();
        var sut = fixture.CreateSut();

        // Act
        await sut.ExportAsync(TestContext.Current.CancellationToken);

        // Assert
        fixture.Windows.Verify(w => w.ShowSaveHtmlFileDialogAsync("Report.html", "scripts"), Times.Once);
        var (path, text) = Assert.Single(fixture.Written);
        Assert.Equal("chosen.html", path);
        Assert.Contains("<div class=\"out\">hello</div>", text);
        Assert.Contains("body { color: red; }", text);
    }

    [Fact]
    public async Task ExportAsync_AScriptNeverSaved_IsOfferedAsResultsInTheStartFolder()
    {
        // Arrange
        var fixture = new ResultsExporterFixture { ScriptPath = null };
        var sut = fixture.CreateSut();

        // Act
        await sut.ExportAsync(TestContext.Current.CancellationToken);

        // Assert
        fixture.Windows.Verify(w => w.ShowSaveHtmlFileDialogAsync("Results.html", "documents"), Times.Once);
    }

    [Fact]
    public async Task ExportAsync_NoResults_SaysSoAndAsksNothing()
    {
        // Arrange
        var fixture = new ResultsExporterFixture { Content = "" };
        var sut = fixture.CreateSut();

        // Act
        await sut.ExportAsync(TestContext.Current.CancellationToken);

        // Assert
        fixture.Windows.Verify(w => w.ShowErrorAsync("Export Results", "There are no results to export."), Times.Once);
        fixture.Windows.Verify(w => w.ShowSaveHtmlFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.Empty(fixture.Written);
    }

    [Fact]
    public async Task ExportAsync_TheDialogCancelled_WritesNothing()
    {
        // Arrange
        var fixture = new ResultsExporterFixture { ChosenPath = null };
        var sut = fixture.CreateSut();

        // Act
        await sut.ExportAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(fixture.Written);
        fixture.Windows.Verify(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task ExportAsync_TheFileCannotBeWritten_ReportsItWithThePathAndTheReason(Type failure)
    {
        // Arrange
        var fixture = new ResultsExporterFixture { WriteFails = (Exception)Activator.CreateInstance(failure, "disk full")! };
        var sut = fixture.CreateSut();

        // Act
        await sut.ExportAsync(TestContext.Current.CancellationToken);

        // Assert
        fixture.Windows.Verify(w => w.ShowErrorAsync("Export Results Failed", "Could not write 'chosen.html':\ndisk full"), Times.Once);
    }

    [Fact]
    public void Build_AStylesheetAndOutput_IsAOneUtf8PageWithAnEncodedTitle()
    {
        // Arrange / Act
        var page = ResultsHtml.Build("p { }", "<b>x</b>", "A & B <results>");

        // Assert
        Assert.StartsWith("<!DOCTYPE html>", page);
        Assert.Contains("<meta charset=\"utf-8\">", page);
        Assert.Contains("<title>A &amp; B &lt;results&gt;</title>", page);
        Assert.Contains("<style>\np { }\n</style>", page);
        Assert.Contains("<body>\n<b>x</b>\n</body>", page);
    }

    [Fact]
    public void Build_AStylesheetThatClosesItself_CannotLetTheRestBeReadAsMarkup()
    {
        // Arrange / Act
        var page = ResultsHtml.Build("a { } </STYLE><script>x()</script>", "", "t");

        // Assert
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "</style", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }
}
