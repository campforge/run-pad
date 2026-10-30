using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class RefactoringServiceTests
{
    [Fact]
    public async Task ReindentOutsideTheWrapperAsync_NothingChanged_ReturnsTheCodeAsItIs()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateRefactorings();

        // Act
        var result = await sut.ReindentOutsideTheWrapperAsync("  var a = 1;", [], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("  var a = 1;", result);
    }

    [Fact]
    public async Task ReindentOutsideTheWrapperAsync_ATouchedLine_IsFormattedAtTheColumnsOfTheEditor()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateRefactorings();
        const string code = "void M()\n{\n        var a = 1;\n}";

        // Act
        var result = await sut.ReindentOutsideTheWrapperAsync(code, [new TextSpan(code.IndexOf("        var", StringComparison.Ordinal), 18)],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("void M()\n{\n    var a = 1;\n}", result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public async Task GetRefactoringsAsync_APositionOutsideTheCode_IsNull(int position)
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateRefactorings();

        // Act
        var result = await sut.GetRefactoringsAsync("var a = 1;", position, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAllMissingUsingsAsync_NothingIsMissing_AddsNoUsingsAndKeepsTheCode()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateRefactorings();

        // Act
        var result = await sut.ResolveAllMissingUsingsAsync("var a = 1;", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("var a = 1;", result.NewCode);
        Assert.Empty(result.UsingsToAdd);
    }

    [Fact]
    public async Task MaterializeFixAsync_AKeyNobodyRegistered_IsNull()
    {
        // Arrange
        using var fixture = new DocumentFormatterFixture();
        var sut = fixture.CreateRefactorings();

        // Act
        var result = await sut.MaterializeFixAsync("var a = 1;", "unknown", TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result);
    }
}
