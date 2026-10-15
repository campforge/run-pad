using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

// A debug stop on an exception used to look exactly like a breakpoint hit ("Paused", nothing about
// the exception). The tab must name the exception in the status and the debugger panel, explain a
// rejected step, and drop all of it once execution resumes.
public class EditorTabItemControlExceptionStopTests
{
    private sealed class StopContext : IBreakpointHitContext
    {
        private readonly TaskCompletionSource<DebugAction> _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DebugAction? ResumedWith => _resumed.Task.IsCompleted ? _resumed.Task.Result : null;
        public Task<DebugAction> Resumed => _resumed.Task;
        public IBreakpoint Breakpoint { get; init; } = Mock.Of<IBreakpoint>(b => b.Line == 1);
        public int ThreadId => 1;
        public IReadOnlyList<IVariableInfo> LocalVariables => [];
        public IReadOnlyList<IStackFrame> CallStack { get; init; } = [];
        public IReadOnlyList<IThreadInfo> Threads => [];
        public DebugStopReason StopReason { get; init; }
        public DebugExceptionInfo? Exception { get; init; }
        public string? ResumeError { get; init; }
        public void Resume(DebugAction action = DebugAction.Continue) => _resumed.TrySetResult(action);
        public Task<IReadOnlyList<IVariableInfo>> GetChildrenAsync(int variablesReference, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IVariableInfo>>([]);
        public Task RunToCursorAsync(int line, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IVariableInfo?> EvaluateAsync(string expression, CancellationToken ct = default) => Task.FromResult<IVariableInfo?>(null);
    }

    private static readonly DebugExceptionInfo Unhandled = new(
        "System.FormatException",
        "The input string 'not-a-number' was not in a correct format.",
        "An unhandled exception of type 'System.FormatException' occurred in System.Private.CoreLib.dll",
        "   at System.Int32.Parse(String s)",
        IsUnhandled: true);

    private static (EditorsTabItemControl Control, EditorTabItemViewModel Vm, Window Host) CreateTab(ILanguageHost? languageHost = null)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());
        var registry = new Mock<ILanguageHostRegistry>();
        if (languageHost is not null)
            registry.Setup(r => r.Create(It.IsAny<string>(), It.IsAny<ILanguageHostOptions>())).Returns(languageHost);
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        var vm = new EditorTabItemViewModel(
            filePath: null, [], prefs.Object, new Mock<IEditorTabActions>().Object, RpadLanguage.CSharp, RpadKind.Statements);
        var control = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object,
            registry.Object, prefs.Object, intelliSense: null, new CSharpReferenceService(),
            new Mock<IBuildCacheService>().Object);
        var host = new Window { Content = control, Width = 1000, Height = 800 };
        host.Show();
        return (control, vm, host);
    }

    private static string? VisibleText(Control root, string name)
    {
        var block = root.GetVisualDescendants().OfType<SelectableTextBlock>().FirstOrDefault(b => b.Name == name);
        return block is not null && block.GetVisualAncestors().OfType<Control>().Prepend(block).All(c => c.IsVisible)
            ? block.Text
            : null;
    }

    [AvaloniaFact]
    public void ExceptionStop_Should_NameTheException_AndClearItOnResume()
    {
        var (control, vm, host) = CreateTab();
        var context = new StopContext { StopReason = DebugStopReason.Exception, Exception = Unhandled };

        control.OnBreakpointHit(context);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RunState.Paused, vm.RunState);
        Assert.Equal("Paused on FormatException", control.State.ExecStatusText);
        Assert.Equal("System.FormatException: The input string 'not-a-number' was not in a correct format.",
            VisibleText(host, "StoppedExceptionText"));
        Assert.Contains("Int32.Parse", VisibleText(host, "StoppedExceptionDetailsText"));
        Assert.Equal("Nothing catches this exception: Continue ends the script with it.", VisibleText(host, "StopNoticeText"));
        Assert.Same(Unhandled, control.State.UnhandledDebugException);

        control.ResumeDebug(DebugAction.Continue);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(DebugAction.Continue, context.ResumedWith);
        Assert.Equal("Process", control.State.ExecStatusText);
        Assert.Null(VisibleText(host, "StoppedExceptionText"));
        Assert.Null(VisibleText(host, "StopNoticeText"));
        host.Close();
    }

    [AvaloniaFact]
    public void RejectedStep_Should_ExplainThatExecutionIsStillStopped()
    {
        var (control, _, host) = CreateTab();

        control.OnBreakpointHit(new StopContext
        {
            StopReason = DebugStopReason.Exception,
            Exception = Unhandled,
            ResumeError = "The debugger couldn't step over: Failed command 'next' : 0x80004005",
        });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            "The debugger couldn't step over: Failed command 'next' : 0x80004005. Execution is still stopped on the exception — use Continue or Stop.",
            VisibleText(host, "StopNoticeText"));
        Assert.Equal("Paused on FormatException", control.State.ExecStatusText);
        host.Close();
    }

    [AvaloniaFact]
    public void BreakpointStop_Should_ShowNoExceptionInfo()
    {
        var (control, _, host) = CreateTab();

        control.OnBreakpointHit(new StopContext { StopReason = DebugStopReason.Breakpoint });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Paused", control.State.ExecStatusText);
        Assert.Null(VisibleText(host, "StoppedExceptionText"));
        Assert.Null(VisibleText(host, "StopNoticeText"));
        Assert.Null(control.State.UnhandledDebugException);
        host.Close();
    }

    // A debug host that stops once on the given exception, then — once resumed — ends the way
    // netcoredbg does after an unhandled exception: the runtime's report on stderr, no failure result.
    private static Mock<ILanguageHost> DebugHostStoppingOn(StopContext stop)
    {
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.Supports(It.IsAny<LanguageHostCapabilities>())).Returns(true);
        host.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        host.Setup(h => h.RunDebugAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, OutputReceivedHandler output, CancellationToken _) =>
            {
                host.Raise(h => h.BreakpointHit += null, stop);
                await stop.Resumed;
                if (stop.Exception is { IsUnhandled: true })
                    output("Unhandled exception. System.FormatException: The input string 'not-a-number' was not in a correct format.", OutputKind.StandardError);
            });
        return host;
    }

    private static async Task<RunState> RunUntilPausedAndContinueAsync(EditorsTabItemControl control, EditorTabItemViewModel vm)
    {
        var run = control.RunAsync(debug: true, new DotnetSdk("10.0.100", "net10.0", "10.0.0", ".NET 10.0"));
        for (var i = 0; i < 200 && vm.RunState != RunState.Paused; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var pausedState = vm.RunState;
        control.ResumeDebug(DebugAction.Continue);
        await run;
        Dispatcher.UIThread.RunJobs();
        return pausedState;
    }

    [AvaloniaFact]
    public async Task ContinuingFromAnUnhandledException_Should_EndTheRunAsUnhandled_NotSuccess()
    {
        var stop = new StopContext { StopReason = DebugStopReason.Exception, Exception = Unhandled };
        var (control, vm, host) = CreateTab(DebugHostStoppingOn(stop).Object);

        Assert.Equal(RunState.Paused, await RunUntilPausedAndContinueAsync(control, vm));

        Assert.Equal(RunState.Idle, vm.RunState);
        Assert.Equal("Unhandled FormatException", control.State.ExecStatusText);
        Assert.Equal("RunPad.Status.ErrorBrush", control.State.ExecStatusColor);
        Assert.Contains("Unhandled exception. System.FormatException", control.State.Output.Content);
        Assert.Null(VisibleText(host, "StoppedExceptionText"));

        host.Close();
    }

    [AvaloniaFact]
    public async Task ContinuingFromACaughtException_Should_EndTheRunAsSuccess()
    {
        var stop = new StopContext { StopReason = DebugStopReason.Exception, Exception = Unhandled with { IsUnhandled = false } };
        var (control, vm, host) = CreateTab(DebugHostStoppingOn(stop).Object);

        Assert.Equal(RunState.Paused, await RunUntilPausedAndContinueAsync(control, vm));

        Assert.Equal("Success", control.State.ExecStatusText);
        host.Close();
    }
}
