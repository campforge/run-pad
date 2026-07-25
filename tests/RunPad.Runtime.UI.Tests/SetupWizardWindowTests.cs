using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Moq;
using RunPad.Contracts;
using RunPad.Services.Preferences;
using RunPad.Services.Setup;
using RunPad.Windows;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public class SetupWizardWindowTests
{
    private static SetupWizardViewModel CreateViewModel(bool hasSdk, bool hasDebugger)
    {
        var installer = new Mock<ISetupInstallerService>();
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());
        var prerequisites = new Mock<IPrerequisiteService>();
        prerequisites.Setup(p => p.Check()).Returns(new PrerequisiteStatus(hasSdk, hasDebugger));
        return new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);
    }

    [AvaloniaFact]
    public void Continue_Click_Should_RaiseTheContinuedEvent()
    {
        var window = new SetupWizardWindow(CreateViewModel(hasSdk: false, hasDebugger: false));
        window.Show();
        var raised = false;
        window.Continued += () => raised = true;

        var continueButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Name == "ContinueButton");
        // The button has no Command bound (plain Click handler) — invoke it the same way a real
        // click would, via the button's routed Click event.
        continueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(raised);
    }

    [AvaloniaFact]
    public void Window_Should_ShowInstallButtons_ForWhicheverPrerequisitesAreMissing()
    {
        var window = new SetupWizardWindow(CreateViewModel(hasSdk: true, hasDebugger: false));
        window.Show();

        var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.Content as string == "Install").ToList();

        // Only the debugger's Install button should be visible — the SDK one is bound to !HasSuitableSdk.
        Assert.Single(buttons, b => b.IsVisible);
    }
}
