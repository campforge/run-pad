using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using RunPad.Services;
using RunPad.Services.Preferences;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// The Exit key quits from every window, not only the main one: pressed elsewhere it closes the main
// window, whose own close asks about everything unsaved.
public class ApplicationExitTests
{
    private static Mock<IPreferencesService> Prefs(KeyboardSettings keyboard)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(keyboard);
        return prefs;
    }

    [AvaloniaFact]
    public void TheExitKey_InAnotherWindow_Should_CloseTheMainWindow()
    {
        var main = new Window();
        var other = new Window { Content = new TextBox() };
        main.Show();
        other.Show();
        var mainClosed = false;
        main.Closed += (_, _) => mainClosed = true;
        using var exit = ApplicationExit.Attach(() => main, Prefs(new KeyboardSettings { IsMac = true }).Object);

        other.KeyPressQwerty(PhysicalKey.Q, RawInputModifiers.Control); // not Exit on macOS
        Assert.False(mainClosed);

        other.KeyPressQwerty(PhysicalKey.Q, RawInputModifiers.Meta);
        Assert.True(mainClosed);
    }

    // The main window dispatches its own keys (and the macOS menu bar's), so the key is not handled
    // a second time here.
    [AvaloniaFact]
    public void TheExitKey_InTheMainWindow_Should_BeLeftToIt()
    {
        var main = new Window();
        main.Show();
        var closing = 0;
        main.Closing += (_, e) =>
        {
            closing++;
            e.Cancel = true;
        };
        using var exit = ApplicationExit.Attach(() => main, Prefs(new KeyboardSettings { IsMac = true }).Object);

        main.KeyPressQwerty(PhysicalKey.Q, RawInputModifiers.Meta);

        Assert.Equal(0, closing);
    }

    [AvaloniaFact]
    public void AReboundExitKey_Should_WorkInAnotherWindow_AndAnUnboundOneNot()
    {
        var keyboard = new KeyboardSettings { IsMac = false }; // Exit is unbound by default here
        var main = new Window();
        var other = new Window();
        main.Show();
        other.Show();
        var mainClosed = false;
        main.Closed += (_, _) => mainClosed = true;
        using var exit = ApplicationExit.Attach(() => main, Prefs(keyboard).Object);

        other.KeyPressQwerty(PhysicalKey.Q, RawInputModifiers.Control);
        Assert.False(mainClosed);

        keyboard.SetKey(KeyCommands.Exit, "Ctrl+Q");
        other.KeyPressQwerty(PhysicalKey.Q, RawInputModifiers.Control);
        Assert.True(mainClosed);
    }
}
