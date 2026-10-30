using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Runtime.UI.Tests.Infrastructure;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.UserControls;

// A run restores the script's packages; afterwards the tab hands its IntelliSense the target
// framework and build directory, so completion and diagnostics use what the restore resolved —
// also when the script then failed to compile.
public class EditorTabPackageReferencesTests
{
    private static readonly DotnetSdk Sdk = new("10.0.100", "net10.0", "10.0.0", ".NET 10.0");

    private static (EditorsTabItemControl Control, Mock<IIntelliSenseService> IntelliSense) CreateControl(RunResult result, bool compiled)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());

        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.RunAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (compiled) host.Raise(h => h.Compiled += null, Path.Combine(Path.GetTempPath(), "missing.dll"));
                return Task.FromResult(result);
            });
        host.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var registry = new Mock<ILanguageHostRegistry>();
        registry.Setup(r => r.Create(It.IsAny<string>(), It.IsAny<ExecutionRequest>())).Returns(host.Object);

        var intelliSense = new Mock<IIntelliSenseService>().ForwardingExecutionContext();
        var vm = new EditorTabItemViewModel(
            null, [Sdk], prefs.Object, new Mock<IEditorTabActions>().Object, MdRunLanguage.CSharp, MdRunKind.Statements);
        var ctrl = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense.Object, new Mock<IBuildCacheService>().Object);
        new Window { Content = ctrl }.Show();
        return (ctrl, intelliSense);
    }

    [AvaloniaFact]
    public async Task ANewTabWithoutPackages_Should_SetItsTargetReferences_BeforeAnyRun()
    {
        var (ctrl, intelliSense) = CreateControl(RunResult.Exited(0), compiled: false);
        try
        {
            await TestWait.EventuallyAsync(() => intelliSense.Verify(s => s.UpdatePackageReferences(
                It.Is<IReadOnlyDictionary<string, Package>>(p => p.Count == 0),
                It.Is<PackageReferenceContext>(c => c.TargetFramework == "net10.0")), Times.AtLeastOnce()));
        }
        finally
        {
            ((Window)TopLevel.GetTopLevel(ctrl)!).Close();
            ctrl.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]   // compiled and ran
    [InlineData(false)]  // the restore ran, but the script did not compile
    public async Task AfterARun_IntelliSense_Should_GetTheTabsBuildDirectory_AndTargetFramework(bool compiled)
    {
        var (ctrl, intelliSense) = CreateControl(compiled ? RunResult.Exited(0) : RunResult.CompilationFailed, compiled);

        await ctrl.RunAsync(debug: false, Sdk);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(ctrl.State.OutputDirectory);
        await TestWait.EventuallyAsync(() => intelliSense.Verify(s => s.UpdatePackageReferences(
            It.IsAny<IReadOnlyDictionary<string, Package>>(),
            It.Is<PackageReferenceContext>(c => c.TargetFramework == "net10.0" && c.OutputDirectory == ctrl.State.OutputDirectory)),
            Times.AtLeastOnce()));
    }
}
