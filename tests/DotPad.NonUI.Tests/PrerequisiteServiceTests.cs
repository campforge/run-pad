using DotPad.Contracts;
using DotPad.Contracts.Models;
using DotPad.Services.Preferences;
using DotPad.Services.Setup;
using Moq;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

public class PrerequisiteServiceTests
{
    private static (Mock<ISdkProvider> Sdk, Mock<IPreferencesService> Prefs, DebuggerSettings Debugger) CreateMocks()
    {
        var sdk = new Mock<ISdkProvider>();
        var debugger = new DebuggerSettings();
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Debugger).Returns(debugger);
        return (sdk, prefs, debugger);
    }

    [Fact]
    public void Check_Should_ReportBothMissing_WhenNoSdkAndNoDebuggerPath()
    {
        var (sdk, prefs, _) = CreateMocks();
        sdk.Setup(s => s.Get()).Returns([]);

        var status = new PrerequisiteService(sdk.Object, prefs.Object).Check();

        Assert.False(status.HasSuitableSdk);
        Assert.False(status.HasValidDebugger);
        Assert.False(status.IsComplete);
    }

    [Fact]
    public void Check_Should_ReportSdkFound_WhenProviderReturnsAtLeastOne()
    {
        var (sdk, prefs, _) = CreateMocks();
        sdk.Setup(s => s.Get()).Returns([new DotnetSdk(".NET", "net10.0", "10.0.0", ".NET 10.0.0")]);

        var status = new PrerequisiteService(sdk.Object, prefs.Object).Check();

        Assert.True(status.HasSuitableSdk);
    }

    [Fact]
    public void Check_Should_ReportSdkMissing_WhenDotnetIsNotOnPathAtAll()
    {
        // DotnetSdkProvider shells out to `dotnet --list-sdks` — when `dotnet` isn't found at all,
        // Process.Start throws Win32Exception rather than returning an empty list.
        var (sdk, prefs, _) = CreateMocks();
        sdk.Setup(s => s.Get()).Throws(new System.ComponentModel.Win32Exception("No such file or directory"));

        var status = new PrerequisiteService(sdk.Object, prefs.Object).Check();

        Assert.False(status.HasSuitableSdk);
    }

    [Fact]
    public void Check_Should_ReportDebuggerMissing_WhenPathIsEmpty()
    {
        var (sdk, prefs, debugger) = CreateMocks();
        sdk.Setup(s => s.Get()).Returns([new DotnetSdk(".NET", "net10.0", "10.0.0", ".NET 10.0.0")]);
        debugger.DebuggerPath = "";

        var status = new PrerequisiteService(sdk.Object, prefs.Object).Check();

        Assert.False(status.HasValidDebugger);
    }

    [Fact]
    public void Check_Should_ReportDebuggerMissing_WhenConfiguredPathDoesNotExistOnDisk()
    {
        var (sdk, prefs, debugger) = CreateMocks();
        sdk.Setup(s => s.Get()).Returns([new DotnetSdk(".NET", "net10.0", "10.0.0", ".NET 10.0.0")]);
        debugger.DebuggerPath = "/definitely/not/a/real/path/netcoredbg";

        var status = new PrerequisiteService(sdk.Object, prefs.Object).Check();

        Assert.False(status.HasValidDebugger);
    }

    [Fact]
    public void Check_Should_ReportComplete_WhenSdkFoundAndDebuggerPathIsARealFile()
    {
        var (sdk, prefs, debugger) = CreateMocks();
        sdk.Setup(s => s.Get()).Returns([new DotnetSdk(".NET", "net10.0", "10.0.0", ".NET 10.0.0")]);
        var realFile = Path.GetTempFileName();
        try
        {
            debugger.DebuggerPath = realFile;

            var status = new PrerequisiteService(sdk.Object, prefs.Object).Check();

            Assert.True(status.IsComplete);
        }
        finally { File.Delete(realFile); }
    }
}
