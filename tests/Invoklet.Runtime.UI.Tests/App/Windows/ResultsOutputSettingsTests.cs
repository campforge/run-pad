using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Results wrapping is set from the gear menu beside the output tabs ("Output settings"), next to
// the exception break options, instead of from Preferences; it is still a saved preference.
public class ResultsOutputSettingsTests
{
    private static readonly DotnetSdk Sdk = new("10.0.100", "net10.0", "10.0.0", ".NET 10.0");

    private static (EditorsTabItemControl Control, Mock<IPreferencesService> Prefs, EditorSettings Editor) Create()
    {
        var editor = new EditorSettings();
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(editor);
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
                output("plain line", OutputKind.StandardOutput);
                output("error line", OutputKind.StandardError);
                return Task.FromResult(RunResult.Exited(0));
            });
        host.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var registry = new Mock<ILanguageHostRegistry>();
        registry.Setup(r => r.Create(It.IsAny<string>(), It.IsAny<ExecutionRequest>())).Returns(host.Object);

        var vm = new EditorTabItemViewModel(
            null, [], prefs.Object, new Mock<IEditorTabActions>().Object, MdRunLanguage.CSharp, MdRunKind.Statements);
        var ctrl = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense: null, new Mock<IBuildCacheService>().Object);
        new Window { Content = ctrl }.Show();
        return (ctrl, prefs, editor);
    }

    [AvaloniaFact]
    public void TheGearMenu_Should_BeOutputSettings_WithTheWrapToggleBesideTheBreakOptions()
    {
        var (ctrl, _, _) = Create();
        var button = ctrl.OutputSettingsButton;
        Assert.Equal("Output settings", ToolTip.GetTip(button));
        Assert.Equal("Output settings", AutomationProperties.GetName(button));

        var boxes = ctrl.OutputSettingsPanel.Children.OfType<CheckBox>().Select(c => c.Content as string).ToList();
        Assert.Equal(
            new[] { "Wrap long lines in results", "Break on unhandled exceptions", "Break on all exceptions (including caught)" },
            boxes);
        Assert.True(ctrl.ResultsWrapCheckBox.IsChecked); // the setting's default

        button.Flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        Assert.True(ctrl.ResultsWrapCheckBox.IsEffectivelyVisible);
        button.Flyout.Hide();
    }

    [AvaloniaFact]
    public void TheToggle_Should_SaveThePreference_AndRestyleThisTabsResultsAtOnce()
    {
        var (ctrl, prefs, editor) = Create();
        Assert.Contains(".out        { white-space:pre-wrap;", ctrl.State.Output.Css);

        ctrl.ResultsWrapCheckBox.IsChecked = false;
        Assert.False(editor.ResultsWordWrap);
        prefs.Verify(p => p.Save(), Times.Once);
        Assert.False(ctrl.ResultsWordWrapForTests);
        Assert.Contains(".out        { white-space:pre; }", ctrl.State.Output.Css);

        ctrl.ResultsWrapCheckBox.IsChecked = true;
        Assert.True(editor.ResultsWordWrap);
        Assert.Contains(".out        { white-space:pre-wrap;", ctrl.State.Output.Css);
    }

    [AvaloniaFact]
    public void AChangeFromAnotherTab_Should_ReachThisTabsToggleAndResults()
    {
        var (ctrl, prefs, editor) = Create();
        editor.ResultsWordWrap = false;
        prefs.Raise(p => p.Changed += null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(ctrl.ResultsWrapCheckBox.IsChecked);
        Assert.False(ctrl.ResultsWordWrapForTests);
        prefs.Verify(p => p.Save(), Times.Never); // following a change doesn't save it again
    }

    [AvaloniaFact]
    public async Task ScriptOutput_Should_UseTheClassesTheWrappingRulesApplyTo()
    {
        var (ctrl, _, _) = Create();
        await ctrl.RunAsync(false, Sdk).WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 20 &&
             (!ctrl.State.Output.Content.Contains("plain line", StringComparison.Ordinal) ||
              !ctrl.State.Output.Content.Contains("error line", StringComparison.Ordinal)); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Contains("class=\"out\"", ctrl.State.Output.Content);
        Assert.Contains("plain line", ctrl.State.Output.Content);
        Assert.Contains("class=\"error\"", ctrl.State.Output.Content);
        Assert.Contains("error line", ctrl.State.Output.Content);
    }
}
