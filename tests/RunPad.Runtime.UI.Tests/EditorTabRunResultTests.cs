using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
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

// The tab's run status used to read "Success" whenever no exception reached the UI — a
// script ending in `Environment.Exit(7)`, or one that didn't even compile, looked successful. The
// status now follows ILanguageHost.RunAsync's RunResult.
public class EditorTabRunResultTests
{
    private static readonly DotnetSdk Sdk = new("10.0.100", "net10.0", "10.0.0", ".NET 10.0");

    private static EditorsTabItemControl CreateControl(RunResult result, string stderrLine = "")
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
            .Returns((string _, OutputReceivedHandler output, CancellationToken _) =>
            {
                output("script output", OutputKind.StandardOutput);
                if (stderrLine.Length > 0) output(stderrLine, OutputKind.StandardError);
                return Task.FromResult(result);
            });
        host.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var registry = new Mock<ILanguageHostRegistry>();
        registry.Setup(r => r.Create(It.IsAny<string>(), It.IsAny<ILanguageHostOptions>())).Returns(host.Object);

        var vm = new EditorTabItemViewModel(
            null, [], prefs.Object, new Mock<IEditorTabActions>().Object, RpadLanguage.CSharp, RpadKind.Statements);
        var ctrl = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense: null, new CSharpReferenceService(), new Mock<IBuildCacheService>().Object);
        new Window { Content = ctrl }.Show();
        return ctrl;
    }

    private static async Task RunAsync(EditorsTabItemControl ctrl)
    {
        await ctrl.RunAsync(debug: false, Sdk);
        Dispatcher.UIThread.RunJobs(); // the posted output and failure message
    }

    [AvaloniaFact]
    public async Task ANonZeroExitCode_Should_FailTheRun_AndShowTheCode_AfterTheScriptsOutput()
    {
        var ctrl = CreateControl(RunResult.Exited(7));

        await RunAsync(ctrl);

        Assert.Equal("Exit code 7", ctrl.State.ExecStatusText);
        Assert.Equal("RunPad.Status.ErrorBrush", ctrl.State.ExecStatusColor);
        var output = ctrl.State.Output.Content;
        Assert.Contains("Process exited with code 7.", output);
        Assert.True(output.IndexOf("script output", StringComparison.Ordinal)
                    < output.IndexOf("Process exited with code 7.", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task ExitCodeZero_Should_Succeed_EvenWithTextOnStderr()
    {
        var ctrl = CreateControl(RunResult.Exited(0), stderrLine: "just a warning");

        await RunAsync(ctrl);

        Assert.Equal("Success", ctrl.State.ExecStatusText);
        Assert.DoesNotContain("Process exited", ctrl.State.Output.Content);
    }

    [AvaloniaFact]
    public async Task ACompilationFailure_Should_NotBeShownAsSuccess()
    {
        var ctrl = CreateControl(RunResult.CompilationFailed);

        await RunAsync(ctrl);

        Assert.Equal("Compilation failed", ctrl.State.ExecStatusText);
        Assert.Equal("RunPad.Status.ErrorBrush", ctrl.State.ExecStatusColor);
    }

    [AvaloniaFact]
    public async Task AStartFailure_Should_FailTheRun_WithAMessage()
    {
        var ctrl = CreateControl(RunResult.StartFailed);

        await RunAsync(ctrl);

        Assert.Equal("Error", ctrl.State.ExecStatusText);
        Assert.Contains("Failed to start the compiled script.", ctrl.State.Output.Content);
    }
}
