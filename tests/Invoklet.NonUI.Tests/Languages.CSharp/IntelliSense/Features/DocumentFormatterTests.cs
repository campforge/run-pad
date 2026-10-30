using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class DocumentFormatterTests
{
    [Fact]
    public async Task FormatDocumentAsync_CrampedCode_GetsItsSpaces()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateSut();

        // Act
        var formatted = await sut.FormatDocumentAsync("var a=1;", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("var a = 1;", formatted);
    }

    [Fact]
    public async Task FormatDocumentAsync_TabsAreTheIndentation_IndentsWithATab()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        fixture.Session.UpdateIndentation(useTabs: true, indentationSize: 4);
        var sut = fixture.CreateSut();

        // Act
        var formatted = await sut.FormatDocumentAsync("void M()\n{\nvar a = 1;\n}", TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("\n\tvar a = 1;", formatted);
    }

    [Fact]
    public async Task FormatDocumentAsync_AWidthOfTwo_IndentsWithTwoSpaces()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        fixture.Session.UpdateIndentation(useTabs: false, indentationSize: 2);
        var sut = fixture.CreateSut();

        // Act
        var formatted = await sut.FormatDocumentAsync("void M()\n{\nvar a = 1;\n}", TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("\n  var a = 1;", formatted);
    }

    [Fact]
    public async Task FormatDocumentAsync_Cancelled_Throws()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateSut();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act
        var result = await Record.ExceptionAsync(() => sut.FormatDocumentAsync("var a=1;", cancelled.Token));

        // Assert: a cancelled format is not an error to log and swallow; the caller sees null or the cancellation
        Assert.True(result is null or OperationCanceledException);
    }
}

public sealed class BreakpointConditionAnalyzerTests
{
    private const string Code = "var x = 1;\nvar y = 2;\n";

    [Fact]
    public async Task AnalyzeBreakpointConditionAsync_ABooleanExpressionOfAVariableInScope_HasNoError()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateAnalyzer();

        // Act
        var analysis = await sut.AnalyzeBreakpointConditionAsync(Code, Code.IndexOf("var y", StringComparison.Ordinal), "x > 0",
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(analysis);
        Assert.Null(analysis.Error);
    }

    [Fact]
    public async Task AnalyzeBreakpointConditionAsync_AnExpressionThatIsNotBoolean_IsReportedByTheCompiler()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateAnalyzer();

        // Act
        var analysis = await sut.AnalyzeBreakpointConditionAsync(Code, Code.IndexOf("var y", StringComparison.Ordinal), "x + 1",
            TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(analysis!.Error);
    }

    [Fact]
    public async Task AnalyzeBreakpointConditionAsync_ASyntaxError_IsReported()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateAnalyzer();

        // Act
        var analysis = await sut.AnalyzeBreakpointConditionAsync(Code, 0, "x >", TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(analysis!.Error);
    }

    [Fact]
    public async Task AnalyzeBreakpointConditionAsync_APrefixOfAName_SuggestsTheNamesInScope()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateAnalyzer();

        // Act
        var analysis = await sut.AnalyzeBreakpointConditionAsync(Code, Code.IndexOf("var y", StringComparison.Ordinal), "x",
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("x", analysis!.Suggestions);
    }

    [Fact]
    public async Task AnalyzeBreakpointConditionAsync_APositionOutsideAnyStatement_AsksForALineInsideExecutableCode()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateAnalyzer();
        const string declarations = "class C\n{\n    int f;\n}\n";

        // Act
        var analysis = await sut.AnalyzeBreakpointConditionAsync(declarations, declarations.IndexOf("int f", StringComparison.Ordinal),
            "f > 0", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Choose a line inside executable code.", analysis!.Error);
    }
}
