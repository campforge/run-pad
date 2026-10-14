using RunPad.Shared;
using Xunit;

namespace RunPad.NonUI.Tests;

// AppKit appends AutoFill, Start Dictation… and Emoji & Symbols to the Edit menu unless these
// switches are set when it starts. This checks that the registration reaches the user defaults
// AppKit reads; the menu bar itself is checked on a real Mac.
public class MacOSEditMenuItemsTests
{
    [Fact]
    public void Disable_Should_RegisterTheSwitchesAppKitReads()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AppKit user defaults exist only on macOS.");

        MacOSEditMenuItems.Disable();

        Assert.True(MacOSEditMenuItems.Read("NSDisabledDictationMenuItem"));
        Assert.True(MacOSEditMenuItems.Read("NSDisabledCharacterPaletteMenuItem"));
        Assert.False(MacOSEditMenuItems.Read("NSAutoFillSystemInsertMenuEnabled"));
    }

    [Fact]
    public void Disable_Should_DoNothing_OffMacOS()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "Covered by the macOS test.");

        MacOSEditMenuItems.Disable(); // no Objective-C runtime to call: must not throw
    }
}
