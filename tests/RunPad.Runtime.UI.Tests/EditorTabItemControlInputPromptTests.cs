using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// EditorsTabItemControl.ShowInputPrompt/
// HideInputPrompt/SubmitInputPromptAsync, the UI half of ILanguageHost.InputRequested/SendInputAsync
// (see CompiledDotnetLanguageHostInputTests.cs for the host-side half). internal (not private) on the
// control itself, same rationale as BuildFindReferencesMenu — headless Avalonia has no reliable way to
// fake a real running script actually asking for input.
public class EditorTabItemControlInputPromptTests
{
    private static Mock<IPreferencesService> MakePrefs()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        return prefs;
    }

    private static Mock<IThemeService> MakeTheme()
    {
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        return theme;
    }

    private static EditorsTabItemControl CreateControl()
    {
        var prefs = MakePrefs();
        var vm = new EditorTabItemViewModel(
            null, [], prefs.Object, new Mock<IEditorTabActions>().Object, RpadLanguage.CSharp, RpadKind.Statements);
        var registry = new Mock<ILanguageHostRegistry>();
        var buildCache = new Mock<IBuildCacheService>();

        var ctrl = new EditorsTabItemControl(vm, MakeTheme().Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense: null, buildCache.Object);
        var window = new Window { Content = ctrl };
        window.Show();
        return ctrl;
    }

    [AvaloniaFact]
    public void ShowInputPrompt_Should_MakeThePromptVisible_WithTheGivenLabel()
    {
        var ctrl = CreateControl();

        ctrl.ShowInputPrompt("Name?", isPassword: false);

        Assert.True(ctrl.IsInputPromptVisible);
        Assert.Equal("Name?", ctrl.InputPromptLabelText);
        Assert.False(ctrl.IsInputPromptMasked);
    }

    [AvaloniaFact]
    public void ShowInputPrompt_Should_DefaultTheLabel_WhenPromptIsNullOrEmpty()
    {
        var ctrl = CreateControl();

        ctrl.ShowInputPrompt(null, isPassword: false);

        Assert.Equal("Input:", ctrl.InputPromptLabelText);
    }

    [AvaloniaFact]
    public void ShowInputPrompt_Should_MaskTheBox_WhenIsPasswordIsTrue()
    {
        var ctrl = CreateControl();

        ctrl.ShowInputPrompt("Password?", isPassword: true);

        Assert.True(ctrl.IsInputPromptMasked);
    }

    [AvaloniaFact]
    public void HideInputPrompt_Should_MakeThePromptInvisible()
    {
        var ctrl = CreateControl();
        ctrl.ShowInputPrompt("Name?", isPassword: false);

        ctrl.HideInputPrompt();

        Assert.False(ctrl.IsInputPromptVisible);
    }

    [AvaloniaFact]
    public async Task SubmitInputPromptAsync_Should_SendTheTypedText_ToTheRunningHost()
    {
        var ctrl = CreateControl();
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.SendInputAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        ctrl.State.RunningHost = host.Object;
        ctrl.ShowInputPrompt("Name?", isPassword: false);
        ctrl.InputPromptBoxText = "Alice";

        await ctrl.SubmitInputPromptAsync();

        host.Verify(h => h.SendInputAsync("Alice", It.IsAny<CancellationToken>()), Times.Once);
    }

    [AvaloniaFact]
    public async Task SubmitInputPromptAsync_Should_HideThePrompt()
    {
        var ctrl = CreateControl();
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.SendInputAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        ctrl.State.RunningHost = host.Object;
        ctrl.ShowInputPrompt("Name?", isPassword: false);
        ctrl.InputPromptBoxText = "Alice";

        await ctrl.SubmitInputPromptAsync();

        Assert.False(ctrl.IsInputPromptVisible);
    }

    [AvaloniaFact]
    public async Task SubmitInputPromptAsync_Should_EchoTheTypedText_ToOutput_WhenNotAPassword()
    {
        var ctrl = CreateControl();
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.SendInputAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        ctrl.State.RunningHost = host.Object;
        ctrl.ShowInputPrompt("Name?", isPassword: false);
        ctrl.InputPromptBoxText = "Alice";

        await ctrl.SubmitInputPromptAsync();

        Assert.Contains("Alice", ctrl.State.Output.Content);
    }

    // A real console never echoes a password either — RunPad's own output shouldn't leak it back.
    [AvaloniaFact]
    public async Task SubmitInputPromptAsync_Should_NotEchoTheTypedText_WhenItIsAPassword()
    {
        var ctrl = CreateControl();
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.SendInputAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        ctrl.State.RunningHost = host.Object;
        ctrl.ShowInputPrompt("Password?", isPassword: true);
        ctrl.InputPromptBoxText = "hunter2";

        await ctrl.SubmitInputPromptAsync();

        Assert.DoesNotContain("hunter2", ctrl.State.Output.Content);
        host.Verify(h => h.SendInputAsync("hunter2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [AvaloniaFact]
    public async Task SubmitInputPromptAsync_Should_DoNothing_WhenNothingIsRunning()
    {
        var ctrl = CreateControl();
        ctrl.ShowInputPrompt("Name?", isPassword: false);
        ctrl.InputPromptBoxText = "Alice";

        await ctrl.SubmitInputPromptAsync(); // State.RunningHost is null — must not throw

        Assert.True(ctrl.IsInputPromptVisible); // never got to the point of hiding/sending
    }
}
