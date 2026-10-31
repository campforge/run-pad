using Avalonia.Input;
using Invoklet.Services.Preferences;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services.Preferences;

public sealed class QuickOpenKeyTests
{
    private static KeyEventArgs CommaWithControl() => new()
    {
        RoutedEvent = InputElement.KeyDownEvent,
        Key = Key.OemComma,
        KeyModifiers = KeyModifiers.Control,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Matches_ControlComma_OpensQuickOpenOnEverySystem(bool mac)
    {
        // Arrange
        var keyboard = new KeyboardSettings { IsMac = mac };

        // Act
        var matches = keyboard.Matches(CommaWithControl(), KeyCommands.QuickOpen);

        // Assert
        Assert.True(matches);
    }

    [Fact]
    public void Matches_CommandComma_StaysWithPreferencesOnMac()
    {
        // Arrange
        var keyboard = new KeyboardSettings { IsMac = true };
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemComma, KeyModifiers = KeyModifiers.Meta };

        // Act
        var quickOpen = keyboard.Matches(e, KeyCommands.QuickOpen);
        var preferences = keyboard.Matches(e, KeyCommands.OpenPreferences);

        // Assert
        Assert.False(quickOpen);
        Assert.True(preferences);
    }

    [Fact]
    public void ConflictsOf_QuickOpen_HasNone()
    {
        // Arrange
        var keyboard = new KeyboardSettings();

        // Act
        var conflicts = keyboard.ConflictsOf(KeyCommands.QuickOpen);

        // Assert
        Assert.Empty(conflicts);
    }
}
