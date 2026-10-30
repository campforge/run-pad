using Invoklet.Contracts.IntelliSense;
using Invoklet.Services;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

// OverloadDescription is a static function of two strings; the parameter is the only thing to fake.
public sealed class OverloadDescriptionTests
{
    private static ISignatureHelpParameter Parameter(string name, string? summary)
    {
        var parameter = new Mock<ISignatureHelpParameter>();
        parameter.SetupGet(p => p.Name).Returns(name);
        parameter.SetupGet(p => p.Summary).Returns(summary);
        return parameter.Object;
    }

    [Fact]
    public void Of_SummaryAndParameterDoc_AreSeparatedByABlankLine()
    {
        // Arrange
        var parameter = Parameter("count", "How many.");

        // Act
        var text = OverloadDescription.Of("Repeats it.", parameter);

        // Assert
        Assert.Equal("Repeats it.\n\ncount: How many.", text);
    }

    [Fact]
    public void Of_OnlyTheMethodSummary_IsThatSummary()
    {
        // Arrange (no active parameter)

        // Act
        var text = OverloadDescription.Of("Repeats it.", null);

        // Assert
        Assert.Equal("Repeats it.", text);
    }

    [Fact]
    public void Of_OnlyTheParameterDoc_IsNameAndSummary()
    {
        // Arrange
        var parameter = Parameter("count", "How many.");

        // Act
        var text = OverloadDescription.Of(null, parameter);

        // Assert
        Assert.Equal("count: How many.", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Of_EmptyParameterDocAndNoSummary_IsNull(string? parameterSummary)
    {
        // Arrange
        var parameter = Parameter("count", parameterSummary);

        // Act
        var text = OverloadDescription.Of("", parameter);

        // Assert
        Assert.Null(text);
    }
}
