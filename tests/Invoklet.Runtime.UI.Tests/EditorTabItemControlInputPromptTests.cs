using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

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

    private static EditorsTabItemControl CreateControl(ILogger<EditorsTabItemControl>? logger = null)
    {
        var prefs = MakePrefs();
        var vm = new EditorTabItemViewModel(
            null, [], prefs.Object, new Mock<IEditorTabActions>().Object, MdRunLanguage.CSharp, MdRunKind.Statements);
        var registry = new Mock<ILanguageHostRegistry>();
        var buildCache = new Mock<IBuildCacheService>();

        var ctrl = new EditorsTabItemControl(vm, MakeTheme().Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense: null, buildCache.Object, logger);
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

    // A real console never echoes a password either — Invoklet's own output shouldn't leak it back.
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

    [AvaloniaFact]
    public async Task FailedSend_Should_ReportWithoutLeakingInput_AndNotFaultTheUiAction()
    {
        var logger = new Mock<ILogger<EditorsTabItemControl>>();
        var ctrl = CreateControl(logger.Object);
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.SendInputAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("private-password"));
        ctrl.State.RunningHost = host.Object;
        ctrl.ShowInputPrompt("Password", true);
        ctrl.InputPromptBoxText = "private-password";
        await ctrl.SubmitInputPromptAsync();
        Assert.Contains("Could not send input", ctrl.State.Output.Content);
        Assert.DoesNotContain("private-password", ctrl.State.Output.Content);
        var log = Assert.Single(logger.Invocations, i => i.Method.Name == "Log");
        Assert.Equal(LogLevel.Error, log.Arguments[0]);
        Assert.Null(log.Arguments[3]);
        Assert.DoesNotContain("private-password", log.Arguments[2]?.ToString());
        Assert.Equal("", ctrl.InputPromptBoxText);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public void Cancel_Should_ClearAndHidePasswordImmediately()
    {
        var ctrl = CreateControl();
        ctrl.ShowInputPrompt("Password", true);
        ctrl.InputPromptBoxText = "private-password";
        ctrl.Cancel();
        Assert.False(ctrl.IsInputPromptVisible);
        Assert.Equal("", ctrl.InputPromptBoxText);
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public async Task PendingSend_Should_ConsumeOnlyOneAnswer_AndNotChangeANewerPrompt()
    {
        var ctrl = CreateControl();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.SendInputAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        ctrl.State.RunningHost = host.Object;
        ctrl.ShowInputPrompt("First", false);
        ctrl.InputPromptBoxText = "answer";
        var send = ctrl.SubmitInputPromptAsync();
        var duplicate = ctrl.SubmitInputPromptAsync();
        var count = host.Invocations.Count(i => i.Method.Name == "SendInputAsync");
        if (count != 1) pending.TrySetResult();
        Assert.Equal(1, count);
        await duplicate;
        ctrl.ShowInputPrompt("Next", true);
        ctrl.InputPromptBoxText = "next-secret";
        pending.SetException(new IOException("old-send"));
        await send;
        Assert.True(ctrl.IsInputPromptVisible);
        Assert.Equal("next-secret", ctrl.InputPromptBoxText);
        Assert.DoesNotContain("Could not send input", ctrl.State.Output.Content);
        ctrl.Dispose();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateSendFailure_AfterStopOrClose_Should_BeQuiet(bool close)
    {
        var logger = new Mock<ILogger<EditorsTabItemControl>>();
        var ctrl = CreateControl(logger.Object);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.SendInputAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        ctrl.State.RunningHost = host.Object;
        ctrl.ShowInputPrompt("Password", true);
        ctrl.InputPromptBoxText = "secret";
        var send = ctrl.SubmitInputPromptAsync();
        if (close) ctrl.Dispose(); else ctrl.Cancel();
        pending.SetException(new ObjectDisposedException("stdin"));
        await send;
        Assert.DoesNotContain("Could not send input", ctrl.State.Output.Content);
        Assert.Empty(logger.Invocations);
        Assert.Equal("", ctrl.InputPromptBoxText);
        ctrl.Dispose();
    }
}
