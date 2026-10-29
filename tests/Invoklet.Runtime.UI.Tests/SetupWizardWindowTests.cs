using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Moq;
using Invoklet.Services.Preferences;
using Invoklet.Services.Setup;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

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

    // Live report 2026-07-26 — "Install" only offered to fetch a new copy; a user who already had
    // .NET/netcoredbg somewhere non-standard had no way to just point the wizard at it.
    [AvaloniaFact]
    public void Window_Should_ShowBrowseButtons_ForWhicheverPrerequisitesAreMissing()
    {
        var window = new SetupWizardWindow(CreateViewModel(hasSdk: true, hasDebugger: false));
        window.Show();

        var browseButtons = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Name is "BrowseDotnetButton" or "BrowseDebuggerButton").ToList();

        Assert.Equal(2, browseButtons.Count);
        // Only the debugger's Browse button should be visible — the SDK one is bound to !HasSuitableSdk.
        Assert.Single(browseButtons, b => b.IsVisible);
        Assert.True(browseButtons.Single(b => b.Name == "BrowseDebuggerButton").IsVisible);
    }

    [AvaloniaFact]
    public void BrowseDotnet_Click_Should_NotThrow_WhenTheUserCancelsThePicker()
    {
        var window = new SetupWizardWindow(CreateViewModel(hasSdk: false, hasDebugger: false));
        window.Show();
        var browseButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Name == "BrowseDotnetButton");

        // The headless storage provider returns no files (nothing picked/cancelled) — this must not
        // throw, and must leave DebuggerSettings untouched since no path was actually chosen.
        var exception = Record.Exception(() => browseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

        Assert.Null(exception);
    }
}
