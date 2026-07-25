using System.Reactive.Linq;
using DotPad.Contracts;
using DotPad.Services.Preferences;
using DotPad.Services.Setup;
using DotPad.Windows.Models;
using Moq;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

public class SetupWizardViewModelTests
{
    private static (Mock<ISetupInstallerService> Installer, Mock<IPreferencesService> Prefs,
        DebuggerSettings Debugger, Mock<IPrerequisiteService> Prerequisites) CreateMocks(PrerequisiteStatus initial)
    {
        var installer = new Mock<ISetupInstallerService>();
        var debugger = new DebuggerSettings();
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Debugger).Returns(debugger);
        var prerequisites = new Mock<IPrerequisiteService>();
        prerequisites.Setup(p => p.Check()).Returns(initial);
        return (installer, prefs, debugger, prerequisites);
    }

    [Fact]
    public void Constructor_Should_ReflectThePrerequisiteServicesInitialStatus()
    {
        var (installer, prefs, _, prerequisites) = CreateMocks(new PrerequisiteStatus(false, false));

        var vm = new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);

        Assert.False(vm.HasSuitableSdk);
        Assert.False(vm.HasValidDebugger);
    }

    [Fact]
    public async Task InstallSdkCommand_Should_ReportsSuccess_AndRefreshStatus_WhenTheInstallerSucceeds()
    {
        var (installer, prefs, _, prerequisites) = CreateMocks(new PrerequisiteStatus(false, false));
        installer
            .Setup(i => i.InstallDotnetSdkAsync(It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/fake/dotnet");
        var vm = new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);
        // Prerequisite check flips to "found" only after the install call — simulates the real
        // PrerequisiteService seeing the newly-installed SDK on the next Check() call.
        prerequisites.Setup(p => p.Check()).Returns(new PrerequisiteStatus(true, false));

        await vm.InstallSdkCommand.Execute();

        Assert.True(vm.HasSuitableSdk);
        Assert.False(vm.IsBusy);
        Assert.Contains("installed", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallSdkCommand_Should_ReportTheErrorInStatusText_AndClearIsBusy_WhenTheInstallerThrows()
    {
        var (installer, prefs, _, prerequisites) = CreateMocks(new PrerequisiteStatus(false, false));
        installer
            .Setup(i => i.InstallDotnetSdkAsync(It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network unreachable"));
        var vm = new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);

        await vm.InstallSdkCommand.Execute();

        Assert.False(vm.IsBusy);
        Assert.Contains("network unreachable", vm.StatusText);
    }

    [Fact]
    public async Task InstallDebuggerCommand_Should_SaveTheResolvedPath_IntoPreferences_WhenTheInstallerSucceeds()
    {
        var (installer, prefs, debugger, prerequisites) = CreateMocks(new PrerequisiteStatus(true, false));
        installer
            .Setup(i => i.InstallNetcoredbgAsync(It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/fake/appdata/DotPad/netcoredbg/netcoredbg");
        var vm = new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);

        await vm.InstallDebuggerCommand.Execute();

        Assert.Equal("/fake/appdata/DotPad/netcoredbg/netcoredbg", debugger.DebuggerPath);
        prefs.Verify(p => p.Save(), Times.Once);
    }

    [Fact]
    public async Task InstallDebuggerCommand_Should_NotTouchPreferences_WhenTheInstallerThrows()
    {
        var (installer, prefs, debugger, prerequisites) = CreateMocks(new PrerequisiteStatus(true, false));
        debugger.DebuggerPath = "";
        installer
            .Setup(i => i.InstallNetcoredbgAsync(It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("download failed"));
        var vm = new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);

        await vm.InstallDebuggerCommand.Execute();

        Assert.Equal("", debugger.DebuggerPath);
        prefs.Verify(p => p.Save(), Times.Never);
    }
}
