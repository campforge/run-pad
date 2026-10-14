using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

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
        registry.Setup(r => r.Create(It.IsAny<string>(), It.IsAny<ILanguageHostOptions>())).Returns(host.Object);

        var intelliSense = new Mock<IIntelliSenseService>();
        var vm = new EditorTabItemViewModel(
            null, [Sdk], prefs.Object, new Mock<IEditorTabActions>().Object, RpadLanguage.CSharp, RpadKind.Statements);
        var ctrl = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense.Object, new CSharpReferenceService(), new Mock<IBuildCacheService>().Object);
        new Window { Content = ctrl }.Show();
        return (ctrl, intelliSense);
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
        intelliSense.Verify(s => s.UpdatePackageReferences(
            It.IsAny<IReadOnlyDictionary<string, Package>>(),
            It.Is<PackageReferenceContext>(c => c.TargetFramework == "net10.0" && c.OutputDirectory == ctrl.State.OutputDirectory)),
            Times.AtLeastOnce());
    }
}
