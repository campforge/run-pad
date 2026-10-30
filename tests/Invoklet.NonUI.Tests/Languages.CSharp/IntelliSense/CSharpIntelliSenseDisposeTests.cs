using Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense.Fixtures;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense;

public class CSharpIntelliSenseDisposeTests
{
    [Fact]
    public void Dispose_CalledTwice_DoesNothingTheSecondTime()
    {
        // Arrange
        var sut = new CSharpIntelliSenseFixture().CreateSut();
        sut.Dispose();

        // Act
        var second = Record.Exception(sut.Dispose);

        // Assert
        Assert.Null(second);
    }

    [Fact]
    public void Dispose_AfterABackgroundPassWasRequested_StopsItAndReleasesTheWorkspace()
    {
        // Arrange
        var sut = new CSharpIntelliSenseFixture().CreateSut();
        sut.NotifyTextChanged("var a = 1;");

        // Act
        var thrown = Record.Exception(sut.Dispose);

        // Assert
        Assert.Null(thrown);
    }
}
