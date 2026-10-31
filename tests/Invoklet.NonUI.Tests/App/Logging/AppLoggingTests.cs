using Invoklet.NonUI.Tests.App.Logging.Fixtures;
using Invoklet.Services.Preferences;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Logging;

public sealed class AppLoggingTests
{
    private static void LogEverything(ILogger logger)
    {
        logger.LogDebug("d");
        logger.LogInformation("i");
        logger.LogWarning("w");
        logger.LogError("e");
        logger.LogCritical("c");
    }

    [Fact]
    public void Configure_Off_WritesNothingAtAnyLevel()
    {
        // Arrange
        using var fixture = new AppLoggingFixture(LogVerbosity.Off);

        // Act
        LogEverything(fixture.Logger);

        // Assert
        Assert.Empty(fixture.Recorder.Levels);
    }

    [Fact]
    public void Configure_FatalOnly_WritesTheCriticalOnes()
    {
        // Arrange
        using var fixture = new AppLoggingFixture(LogVerbosity.Fatal);

        // Act
        LogEverything(fixture.Logger);

        // Assert
        Assert.Equal([LogLevel.Critical], fixture.Recorder.Levels);
    }

    [Fact]
    public void Configure_Information_WritesFromInformationUp()
    {
        // Arrange
        using var fixture = new AppLoggingFixture(LogVerbosity.Information);

        // Act
        LogEverything(fixture.Logger);

        // Assert
        Assert.Equal([LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical], fixture.Recorder.Levels);
    }

    [Fact]
    public void Configure_TheVerbosityChangesLater_AffectsLoggersThatAlreadyExist()
    {
        // Arrange
        using var fixture = new AppLoggingFixture(LogVerbosity.Fatal);
        var logger = fixture.Logger;
        logger.LogError("before");

        // Act
        fixture.Switch.Apply(LogVerbosity.Error);
        logger.LogError("after");

        // Assert
        Assert.Equal([LogLevel.Error], fixture.Recorder.Levels);
    }
}
