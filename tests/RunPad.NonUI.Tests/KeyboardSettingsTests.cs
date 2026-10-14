using System.Text.Json;
using Avalonia.Input;
using RunPad.Services.Preferences;
using Xunit;

namespace RunPad.NonUI.Tests;

public class KeyboardSettingsTests
{
    [Fact]
    public void Defaults_Should_DependOnThePlatform()
    {
        var windows = new KeyboardSettings { IsMac = false };
        var mac = new KeyboardSettings { IsMac = true };

        Assert.Equal("Ctrl+S", windows.KeyFor(KeyCommands.Save));
        Assert.Equal("Cmd+S", mac.KeyFor(KeyCommands.Save));
        Assert.Equal("F5", mac.KeyFor(KeyCommands.RunOrContinue)); // no macOS variant: same key
        Assert.Equal(KeyModifiers.Meta, mac.GestureFor(KeyCommands.Save)!.KeyModifiers);
        Assert.Equal(KeyModifiers.Meta, mac.CommandModifier);
        Assert.Equal(KeyModifiers.Control, windows.CommandModifier);
    }

    // Every default must parse — a typo would silently leave a command without its key.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryDefaultKey_Should_Parse(bool mac)
    {
        var keyboard = new KeyboardSettings { IsMac = mac };
        foreach (var command in KeyCommands.All)
        {
            var key = keyboard.KeyFor(command);
            if (key.Length > 0) Assert.True(keyboard.GestureFor(command) is not null, $"{command.Id}: '{key}'");
        }
        Assert.Equal(KeyCommands.All.Count, KeyCommands.All.Select(c => c.Id).Distinct().Count());
    }

    // The defaults themselves must not collide (each would steal the other's keypress).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Defaults_Should_NotConflict(bool mac)
    {
        var keyboard = new KeyboardSettings { IsMac = mac };
        Assert.All(KeyCommands.All, c => Assert.Empty(keyboard.ConflictsOf(c)));
    }

    [Fact]
    public void SetKey_Should_StoreOnlyChangesFromTheDefault()
    {
        var keyboard = new KeyboardSettings { IsMac = false };

        keyboard.SetKey(KeyCommands.Stop, "escape");
        keyboard.SetKey(KeyCommands.Save, "Ctrl+S");
        keyboard.SetKey(KeyCommands.Pause, "");

        Assert.Equal(new Dictionary<string, string> { [KeyCommands.Stop.Id] = "Esc" }, keyboard.Bindings);
        keyboard.SetKey(KeyCommands.Stop, null);
        Assert.Empty(keyboard.Bindings);
    }

    [Fact]
    public void AnEmptyBinding_Should_UnbindTheCommand()
    {
        var keyboard = new KeyboardSettings { IsMac = false };
        keyboard.Bindings[KeyCommands.Save.Id] = "";

        Assert.Equal("", keyboard.KeyFor(KeyCommands.Save));
        Assert.Null(keyboard.GestureFor(KeyCommands.Save));
    }

    [Theory]
    [InlineData("Ctrl+]", Key.OemCloseBrackets, KeyModifiers.Control)]
    [InlineData("Cmd+,", Key.OemComma, KeyModifiers.Meta)]
    [InlineData("Ctrl+=", Key.OemPlus, KeyModifiers.Control)]
    [InlineData("Ctrl++", Key.OemPlus, KeyModifiers.Control)]
    [InlineData("Ctrl+OemCloseBrackets", Key.OemCloseBrackets, KeyModifiers.Control)]
    [InlineData("Cmd+Backspace", Key.Back, KeyModifiers.Meta)]
    [InlineData("Enter", Key.Return, KeyModifiers.None)]
    [InlineData("shift+f5", Key.F5, KeyModifiers.Shift)]
    public void KeyGestureText_Should_ParseReadableAndAvaloniaNames(string text, Key key, KeyModifiers modifiers)
    {
        var gesture = KeyGestureText.Parse(text);

        Assert.NotNull(gesture);
        Assert.Equal(key, gesture!.Key);
        Assert.Equal(modifiers, gesture.KeyModifiers);
    }

    [Theory]
    [InlineData("ctrl+oemclosebrackets", "Ctrl+]")]
    [InlineData("Alt+Shift+F", "Shift+Alt+F")]
    [InlineData("Meta+S", "Cmd+S")]
    [InlineData("Escape", "Esc")]
    public void KeyGestureText_Should_Normalize(string text, string expected)
        => Assert.Equal(expected, KeyGestureText.Normalize(text));

    // Settings written before 0.5 stored every binding as its own property, defaults included.
    [Fact]
    public void MigrateLegacy_Should_KeepOnlyChangedBindings()
    {
        const string json = """
            { "Keyboard": { "Run": "F5", "Cancel": "Escape", "Save": "Ctrl+S", "CloseTab": "Ctrl+Shift+Q", "NewFile": "" } }
            """;
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        var keyboard = settings.Keyboard;
        keyboard.IsMac = true;

        Assert.True(keyboard.MigrateLegacy());

        Assert.Equal(new Dictionary<string, string>
        {
            [KeyCommands.CloseTab.Id] = "Ctrl+Shift+Q",
            [KeyCommands.NewFile.Id] = "",
        }, keyboard.Bindings);
        Assert.Null(keyboard.Legacy);
        Assert.Equal("Shift+F5", keyboard.KeyFor(KeyCommands.Stop)); // the old default Esc is not kept
        Assert.Equal("Cmd+S", keyboard.KeyFor(KeyCommands.Save));     // the old Ctrl default is not pinned
        Assert.False(keyboard.MigrateLegacy());

        var saved = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("\"Run\"", saved);
        Assert.Contains("\"Bindings\"", saved);
    }
}
