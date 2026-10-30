using Invoklet.NonUI.Tests.App.Windows.Models.Fixtures;
using Invoklet.Services.Preferences;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Windows.Models;

public class PreferencesWindowViewModelLoggingTests
{
    [Fact]
    public void LogVerbosityChoices_Offer_EveryLevelWithOffAndFatalFirst()
    {
        // Arrange
        var sut = new PreferencesWindowViewModelLoggingFixture().CreateSut();

        // Act
        var levels = sut.LogVerbosityChoices.Select(c => c.Value).ToArray();

        // Assert
        Assert.Equal(Enum.GetValues<LogVerbosity>(), levels);
        Assert.Equal([LogVerbosity.Off, LogVerbosity.Fatal], levels.Take(2));
    }

    [Fact]
    public void SelectedLogVerbosity_New_IsTheSavedLevel()
    {
        // Arrange
        var fixture = new PreferencesWindowViewModelLoggingFixture();
        fixture.Logging.Level = LogVerbosity.Warning;

        // Act
        var sut = fixture.CreateSut();

        // Assert
        Assert.Equal(LogVerbosity.Warning, sut.SelectedLogVerbosity.Value);
    }

    [Fact]
    public void SelectedLogVerbosity_Chosen_IsStoredAndSaved()
    {
        // Arrange
        var fixture = new PreferencesWindowViewModelLoggingFixture();
        var sut = fixture.CreateSut();

        // Act
        sut.SelectedLogVerbosity = LogVerbosityChoice.All.Single(c => c.Value == LogVerbosity.Off);

        // Assert
        Assert.Equal(LogVerbosity.Off, fixture.Logging.Level);
        fixture.Prefs.Verify(p => p.Save(), Times.Once);
    }

    [Fact]
    public void SelectedLogVerbosity_TheLevelItAlreadyHas_SavesNothing()
    {
        // Arrange
        var fixture = new PreferencesWindowViewModelLoggingFixture();
        var sut = fixture.CreateSut();

        // Act
        sut.SelectedLogVerbosity = LogVerbosityChoice.All.Single(c => c.Value == LogVerbosity.Information);

        // Assert
        fixture.Prefs.Verify(p => p.Save(), Times.Never);
    }

    [Fact]
    public void SelectedNode_Logging_ShowsTheLoggingPage()
    {
        // Arrange
        var sut = new PreferencesWindowViewModelLoggingFixture().CreateSut();

        // Act
        sut.SelectedNode = sut.Nodes.Single(n => n.Id == "logging");

        // Assert
        Assert.True(sut.IsLoggingSelected);
        Assert.False(sut.IsExperimentalSelected);
    }
}
