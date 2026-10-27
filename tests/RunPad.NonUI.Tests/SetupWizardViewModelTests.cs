using System.Reactive.Linq;
using Moq;
using RunPad.Services.Preferences;
using RunPad.Services.Setup;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

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
            .Setup(i => i.InstallDotnetSdkAsync(It.IsAny<IProgress<SetupProgress>>(), It.IsAny<CancellationToken>()))
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
            .Setup(i => i.InstallDotnetSdkAsync(It.IsAny<IProgress<SetupProgress>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network unreachable"));
        var vm = new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);

        await vm.InstallSdkCommand.Execute();

        Assert.False(vm.IsBusy);
        Assert.Contains("network unreachable", vm.StatusText);
    }

    // The installer only puts the SDK on this process's PATH; without the saved path a restart
    // (on macOS, any start from Finder or the Dock) couldn't find the SDK it had just installed.
    [Fact]
    public async Task InstallSdkCommand_Should_SaveTheInstalledDotnetPath_IntoPreferences()
    {
        var (installer, prefs, debugger, prerequisites) = CreateMocks(new PrerequisiteStatus(false, false));
        installer
            .Setup(i => i.InstallDotnetSdkAsync(It.IsAny<IProgress<SetupProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/home/user/.dotnet/dotnet");
        var vm = new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);

        await vm.InstallSdkCommand.Execute();

        Assert.Equal("/home/user/.dotnet/dotnet", debugger.DotnetPath);
        prefs.Verify(p => p.Save(), Times.Once);
    }

    [Fact]
    public async Task InstallSdkCommand_Should_KeepTheDotnetPath_WhenTheInstallFails()
    {
        var (installer, prefs, debugger, prerequisites) = CreateMocks(new PrerequisiteStatus(false, false));
        debugger.DotnetPath = "/custom/dotnet";
        installer
            .Setup(i => i.InstallDotnetSdkAsync(It.IsAny<IProgress<SetupProgress>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("dotnet-install exited with code 1."));
        var vm = new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);

        await vm.InstallSdkCommand.Execute();

        Assert.Equal("/custom/dotnet", debugger.DotnetPath);
        prefs.Verify(p => p.Save(), Times.Never);
    }

    [Fact]
    public async Task InstallDebuggerCommand_Should_SaveTheResolvedPath_IntoPreferences_WhenTheInstallerSucceeds()
    {
        var (installer, prefs, debugger, prerequisites) = CreateMocks(new PrerequisiteStatus(true, false));
        installer
            .Setup(i => i.InstallNetcoredbgAsync(It.IsAny<IProgress<SetupProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/fake/appdata/RunPad/netcoredbg/netcoredbg");
        var vm = new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);

        await vm.InstallDebuggerCommand.Execute();

        Assert.Equal("/fake/appdata/RunPad/netcoredbg/netcoredbg", debugger.DebuggerPath);
        prefs.Verify(p => p.Save(), Times.Once);
    }

    // Live report 2026-07-27 — the last install's confirmation text ("netcoredbg installed.") used to
    // sit there forever, looking stale/unfinished, even once there was nothing left for the wizard to
    // do. It should clear itself once both rows are satisfied.
    [Fact]
    public async Task InstallDebuggerCommand_Should_ClearStatusText_WhenBothPrerequisitesEndUpSatisfied()
    {
        var (installer, prefs, _, prerequisites) = CreateMocks(new PrerequisiteStatus(true, false));
        installer
            .Setup(i => i.InstallNetcoredbgAsync(It.IsAny<IProgress<SetupProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/fake/appdata/RunPad/netcoredbg/netcoredbg");
        var vm = new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);
        prerequisites.Setup(p => p.Check()).Returns(new PrerequisiteStatus(true, true));

        await vm.InstallDebuggerCommand.Execute();

        Assert.Equal("", vm.StatusText);
    }

    // A message that means the user still has something to fix (a failed/timed-out/cancelled install)
    // must never be wiped out just because HasSuitableSdk/HasValidDebugger happen to both read true.
    [Fact]
    public async Task InstallDebuggerCommand_Should_KeepTheErrorMessage_EvenIfBothPrerequisitesReadTrue()
    {
        var (installer, prefs, _, prerequisites) = CreateMocks(new PrerequisiteStatus(true, true));
        installer
            .Setup(i => i.InstallNetcoredbgAsync(It.IsAny<IProgress<SetupProgress>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("archive extracted but the exe wasn't found"));
        var vm = new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);

        await vm.InstallDebuggerCommand.Execute();

        Assert.Contains("archive extracted", vm.StatusText);
    }

    [Fact]
    public async Task InstallDebuggerCommand_Should_NotTouchPreferences_WhenTheInstallerThrows()
    {
        var (installer, prefs, debugger, prerequisites) = CreateMocks(new PrerequisiteStatus(true, false));
        debugger.DebuggerPath = "";
        installer
            .Setup(i => i.InstallNetcoredbgAsync(It.IsAny<IProgress<SetupProgress>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("download failed"));
        var vm = new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);

        await vm.InstallDebuggerCommand.Execute();

        Assert.Equal("", debugger.DebuggerPath);
        prefs.Verify(p => p.Save(), Times.Never);
    }

    // Live report 2026-07-26 — the wizard only offered "install for me"; a user with .NET/netcoredbg
    // already somewhere (just not on PATH, or a specific one of several installs) had no way to just
    // point at it.
    [Fact]
    public void SetDotnetPathManually_Should_SavePathIntoPreferences_AndRefreshStatus()
    {
        var (installer, prefs, debugger, prerequisites) = CreateMocks(new PrerequisiteStatus(false, false));
        var vm = new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);
        prerequisites.Setup(p => p.Check()).Returns(new PrerequisiteStatus(true, false));

        vm.SetDotnetPathManually("/custom/place/dotnet");

        Assert.Equal("/custom/place/dotnet", debugger.DotnetPath);
        prefs.Verify(p => p.Save(), Times.Once);
        Assert.True(vm.HasSuitableSdk);
    }

    [Fact]
    public void SetDebuggerPathManually_Should_SavePathIntoPreferences_AndRefreshStatus()
    {
        var (installer, prefs, debugger, prerequisites) = CreateMocks(new PrerequisiteStatus(true, false));
        var vm = new SetupWizardViewModel(installer.Object, prefs.Object, prerequisites.Object);
        prerequisites.Setup(p => p.Check()).Returns(new PrerequisiteStatus(true, true));

        vm.SetDebuggerPathManually("/custom/place/netcoredbg");

        Assert.Equal("/custom/place/netcoredbg", debugger.DebuggerPath);
        prefs.Verify(p => p.Save(), Times.Once);
        Assert.True(vm.HasValidDebugger);
    }
}
