using System.Text.Json;
using Invoklet.Services.Preferences;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services.Preferences;

public sealed class LoggingSettingsTests
{
    [Fact]
    public void New_Settings_LogInformationLikeBefore()
    {
        // Arrange / Act
        var settings = new AppSettings();

        // Assert
        Assert.Equal(LogVerbosity.Information, settings.Logging.Level);
    }

    [Fact]
    public void Serialize_TheLevel_IsStoredByName()
    {
        // Arrange
        var settings = new AppSettings { Logging = { Level = LogVerbosity.Fatal } };

        // Act
        var json = JsonSerializer.Serialize(settings);

        // Assert
        Assert.Contains("\"Level\":\"Fatal\"", json);
    }

    [Fact]
    public void Deserialize_ASettingsFileWithoutLogging_KeepsTheDefault()
    {
        // Arrange / Act
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"Editor\":{}}")!;

        // Assert
        Assert.Equal(LogVerbosity.Information, settings.Logging.Level);
    }

    [Fact]
    public void Deserialize_ALevelByName_IsRead()
    {
        // Arrange / Act
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"Logging\":{\"Level\":\"Off\"}}")!;

        // Assert
        Assert.Equal(LogVerbosity.Off, settings.Logging.Level);
    }

    [Theory]
    [InlineData(LogVerbosity.Off, LogLevel.None)]
    [InlineData(LogVerbosity.Fatal, LogLevel.Critical)]
    [InlineData(LogVerbosity.Error, LogLevel.Error)]
    [InlineData(LogVerbosity.Warning, LogLevel.Warning)]
    [InlineData(LogVerbosity.Information, LogLevel.Information)]
    [InlineData(LogVerbosity.Debug, LogLevel.Debug)]
    public void MinimumLevel_EachVerbosity_MapsToItsLogLevel(LogVerbosity verbosity, LogLevel expected)
        => Assert.Equal(expected, verbosity.MinimumLevel());
}
