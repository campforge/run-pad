using Invoklet.Logging;
using Invoklet.Services.Preferences;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Logging;

public sealed class LogLevelSwitchTests
{
    [Theory]
    [InlineData(LogVerbosity.Off, LogLevel.Critical, false)]
    [InlineData(LogVerbosity.Fatal, LogLevel.Critical, true)]
    [InlineData(LogVerbosity.Fatal, LogLevel.Error, false)]
    [InlineData(LogVerbosity.Error, LogLevel.Error, true)]
    [InlineData(LogVerbosity.Error, LogLevel.Warning, false)]
    [InlineData(LogVerbosity.Warning, LogLevel.Warning, true)]
    [InlineData(LogVerbosity.Warning, LogLevel.Information, false)]
    [InlineData(LogVerbosity.Information, LogLevel.Information, true)]
    [InlineData(LogVerbosity.Information, LogLevel.Debug, false)]
    [InlineData(LogVerbosity.Debug, LogLevel.Debug, true)]
    [InlineData(LogVerbosity.Debug, LogLevel.Trace, false)]
    [InlineData(LogVerbosity.Debug, LogLevel.Critical, true)]
    public void IsEnabled_ALevelAgainstTheVerbosity_IsWrittenOnlyFromTheMinimumUp(LogVerbosity verbosity, LogLevel level, bool expected)
    {
        // Arrange
        var sut = new LogLevelSwitch(verbosity);

        // Act
        var enabled = sut.IsEnabled(level);

        // Assert
        Assert.Equal(expected, enabled);
    }

    [Fact]
    public void IsEnabled_LevelNone_IsNeverWritten()
    {
        // Arrange
        var sut = new LogLevelSwitch(LogVerbosity.Debug);

        // Act
        var enabled = sut.IsEnabled(LogLevel.None);

        // Assert
        Assert.False(enabled);
    }

    [Fact]
    public void Apply_ANewVerbosity_ChangesWhatIsWrittenFromThenOn()
    {
        // Arrange
        var sut = new LogLevelSwitch(LogVerbosity.Information);

        // Act
        sut.Apply(LogVerbosity.Off);

        // Assert
        Assert.False(sut.IsEnabled(LogLevel.Critical));
        Assert.Equal(LogVerbosity.Off, sut.Verbosity);
    }

    [Fact]
    public void New_WithoutAVerbosity_IsInformation()
    {
        // Arrange / Act
        var sut = new LogLevelSwitch();

        // Assert
        Assert.Equal(LogVerbosity.Information, sut.Verbosity);
    }
}
