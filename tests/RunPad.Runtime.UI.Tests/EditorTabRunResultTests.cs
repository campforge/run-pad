using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Moq;
using RunPad.Avalonia.CodeTextEditor;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.Connections;
using RunPad.Contracts.Models;
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

// The tab's run status used to read "Success" whenever no exception reached the UI — a
// script ending in `Environment.Exit(7)`, or one that didn't even compile, looked successful. The
// status now follows ILanguageHost.RunAsync's RunResult.
public class EditorTabRunResultTests
{
    private static readonly DotnetSdk Sdk = new("10.0.100", "net10.0", "10.0.0", ".NET 10.0");

    private static EditorsTabItemControl CreateControl(
        RunResult result, string stderrLine = "",
        RpadLanguage language = RpadLanguage.CSharp, List<(string Language, ExecutionRequest Request)>? created = null, Func<Task<RunResult>>? pending = null, ILogger<EditorsTabItemControl>? logger = null, Action<OutputReceivedHandler>? captureOutput = null,
        Action<Mock<ILanguageHost>>? configureHost = null, ConnectionSchemas? connectionSchemas = null, ConnectionDriverRegistry? connectionDrivers = null)
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
                captureOutput?.Invoke(output);
                output("script output", OutputKind.StandardOutput);
                if (stderrLine.Length > 0) output(stderrLine, OutputKind.StandardError);
                return pending?.Invoke() ?? Task.FromResult(result);
            });
        host.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        configureHost?.Invoke(host);
        var registry = new Mock<ILanguageHostRegistry>();
        registry.Setup(r => r.Create(It.IsAny<string>(), It.IsAny<ExecutionRequest>()))
            .Callback((string id, ExecutionRequest request) => created?.Add((id, request)))
            .Returns(host.Object);

        var vm = new EditorTabItemViewModel(
            null, [], prefs.Object, new Mock<IEditorTabActions>().Object, language, RpadKind.Statements);
        var ctrl = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
            intelliSense: null, new Mock<IBuildCacheService>().Object, logger,
            connectionDrivers, connectionSchemas);
        new Window { Content = ctrl }.Show();
        return ctrl;
    }

    [AvaloniaFact]
    public void BreakpointConditionDialog_Should_InheritTemplatesAndTrackTheOwnersPalette()
    {
        var theme = new RunPadTheme();
        global::Avalonia.Application.Current!.Styles.Add(theme);
        var ctrl = CreateControl(RunResult.Exited(0));
        var window = (Window)TopLevel.GetTopLevel(ctrl)!;
        try
        {
            window.RequestedThemeVariant = RunPadThemeVariants.RiderDark;
            ctrl.State.Editor.RaiseBreakpointRightClicked(1, null);
            var dialog = Assert.IsType<global::RunPad.Windows.BreakpointConditionDialog>(ctrl.BreakpointDialog);
            Assert.True(dialog.IsVisible);
            var panel = dialog.ConditionEditor;
            foreach (var variant in new[] { RunPadThemeVariants.RiderDark, RunPadThemeVariants.RiderLight,
                RunPadThemeVariants.RunPadMidnight, RunPadThemeVariants.RunPadWarmLight,
                RunPadThemeVariants.VsCodeDark, RunPadThemeVariants.VsCodeLight,
                RunPadThemeVariants.Vs2022Dark, RunPadThemeVariants.Vs2022Light })
            {
                window.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(variant, panel.ActualThemeVariant);
                theme.TryGetResource("RunPad.Panel.BackgroundBrush", variant, out var expected);
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(expected).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(dialog.FindControl<Border>("DialogSurface")!.Background).Color);
                Assert.NotNull(panel.FindControl<StackPanel>("ContentLayout"));
                var input = panel.FindControl<TextBox>("ConditionBox")!;
                Assert.NotEmpty(input.GetVisualDescendants());
                theme.TryGetResource("RunPad.Editor.BackgroundBrush", variant, out var expectedInput);
                var inputBorder = input.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_BorderElement");
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(expectedInput).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(inputBorder.Background).Color);
                Assert.NotEmpty(panel.FindControl<CheckBox>("EnabledBox")!.GetVisualDescendants());
                Assert.NotEmpty(panel.FindControl<Button>("DoneButton")!.GetVisualDescendants());
                Assert.Null(panel.FindControl<Expander>("HitCountOptions"));
                Assert.InRange(panel.Bounds.Width, 360, 420);
            }
        }
        finally { ctrl.Dispose(); window.Close(); global::Avalonia.Application.Current!.Styles.Remove(theme); }
    }

    [AvaloniaFact]
    public async Task BreakpointApplyFailure_ShouldBeLogged_WithoutEscapingTheUIBoundary()
    {
        var logger = new Mock<ILogger<EditorsTabItemControl>>();
        var ctrl = CreateControl(RunResult.Exited(0), logger: logger.Object);
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.Supports(LanguageHostCapabilities.Breakpoints)).Returns(true);
        host.Setup(h => h.RemoveBreakpoint(It.IsAny<RunPad.Contracts.IBreakpoint>()))
            .Throws(new InvalidOperationException("breakpoint update failed"));
        ctrl.State.RunningHost = host.Object;
        ctrl.State.SourceFilePath = "RunPadQuery.cs";
        try
        {
            ctrl.State.Editor.RaiseBreakpointRightClicked(1, null);
            var dialog = Assert.IsType<global::RunPad.Windows.BreakpointConditionDialog>(ctrl.BreakpointDialog);
            dialog.Close(((string?)null, (string?)null, true));
            await TestWait.WhileAsync(() => !logger.Invocations.Any(i => i.Method.Name == nameof(ILogger.Log)));
            Assert.Single(logger.Invocations, i => i.Method.Name == nameof(ILogger.Log)
                && Equals(i.Arguments[0], LogLevel.Error));
            Assert.Contains("breakpoint update failed", ctrl.State.Output.Content);
            Assert.Null(ctrl.BreakpointDialog);
        }
        finally { ctrl.Dispose(); ((Window)TopLevel.GetTopLevel(ctrl)!).Close(); }
    }

    [AvaloniaFact]
    public void BreakpointEditor_Should_OpenInAnOwnedDialog_AndCloseWithItsTab()
    {
        var ctrl = CreateControl(RunResult.Exited(0));
        try
        {
            ctrl.State.Editor.RaiseBreakpointRightClicked(1, null);
            var dialog = Assert.IsType<global::RunPad.Windows.BreakpointConditionDialog>(ctrl.BreakpointDialog);
            Assert.True(dialog.IsVisible);
            Assert.Same(TopLevel.GetTopLevel(ctrl), dialog.Owner);
            Assert.IsType<BreakpointConditionEditor>(dialog.ConditionEditor);
            ctrl.Dispose();
            Assert.False(dialog.IsVisible);
            Assert.Empty(ctrl.State.Editor.Breakpoints);
        }
        finally { ((Window)TopLevel.GetTopLevel(ctrl)!).Close(); }
    }

    [AvaloniaFact]
    public void EditingBreakpointOptions_Should_ReplaceTheRunningHostsBreakpoint_AndRespectEnabled()
    {
        var ctrl = CreateControl(RunResult.Exited(0));
        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.Supports(LanguageHostCapabilities.Breakpoints)).Returns(true);
        ctrl.State.RunningHost = host.Object;
        ctrl.State.SourceFilePath = "RunPadQuery.cs";
        try
        {
            ctrl.ApplyBreakpointOptions(2, " count > 5 ", "% 2", true);
            host.Verify(h => h.AddBreakpoint(It.Is<RunPad.Contracts.IBreakpoint>(b =>
                b.Line == 2 && b.Condition == "count > 5" && b.HitCondition == "% 2")), Times.Once);
            Assert.Single(ctrl.State.Editor.Breakpoints);
            host.Invocations.Clear();
            ctrl.ApplyBreakpointOptions(2, "count == 9", null, false);
            host.Verify(h => h.RemoveBreakpoint(It.IsAny<RunPad.Contracts.IBreakpoint>()), Times.Once);
            host.Verify(h => h.AddBreakpoint(It.IsAny<RunPad.Contracts.IBreakpoint>()), Times.Never);
            Assert.False(Assert.Single(ctrl.State.Editor.Breakpoints).IsEnabled);
            ctrl.ApplyBreakpointOptions(2, " ", null, true);
            host.Verify(h => h.AddBreakpoint(It.Is<RunPad.Contracts.IBreakpoint>(b => b.Condition == null)), Times.Once);
        }
        finally
        {
            ctrl.State.RunningHost = null;
            ((Window)TopLevel.GetTopLevel(ctrl)!).Close();
            ctrl.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task StopThenImmediateRun_Should_NotReplaceTheStillUnwindingRun()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<RunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = new List<(string Language, ExecutionRequest Request)>();
        var ctrl = CreateControl(RunResult.Exited(0), created: created, pending: () => { started.TrySetResult(); return finish.Task; });
        var first = ctrl.RunAsync(false, Sdk);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var original = ctrl.State.RunCts;
        ctrl.Cancel();
        var second = ctrl.RunAsync(false, Sdk);
        try
        {
            Assert.Same(original, ctrl.State.RunCts);
            Assert.Single(created);
        }
        finally { finish.TrySetResult(RunResult.Exited(0)); }
        try
        {
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(ctrl.State.RunCts);
            await ctrl.RunAsync(false, Sdk).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, created.Count);
            Assert.Equal("Success", ctrl.State.ExecStatusText);
        }
        finally
        {
            ((Window)TopLevel.GetTopLevel(ctrl)!).Close();
            ctrl.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task ClosingARunningTab_Should_NotFaultItsUnwindingRun()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<RunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctrl = CreateControl(RunResult.Exited(0), pending: () => { started.TrySetResult(); return finish.Task; });
        var run = ctrl.RunAsync(false, Sdk);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ((Window)TopLevel.GetTopLevel(ctrl)!).Close();
        var token = ctrl.State.RunCts!.Token;
        ctrl.Dispose();
        Assert.True(token.IsCancellationRequested);
        _ = ctrl.State.RunCts!.Token; // still owned by the unwinding run, not disposed by the tab
        finish.TrySetResult(RunResult.Exited(0));
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [AvaloniaFact]
    public async Task ARunCleanupFailure_Should_BeLoggedAndReported_WithoutEscapingTheUIBoundary()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<RunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new Mock<ILogger<EditorsTabItemControl>>();
        var ctrl = CreateControl(RunResult.Exited(0), pending: () => { started.TrySetResult(); return finish.Task; }, logger: logger.Object);
        var run = ctrl.RunAsync(false, Sdk);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ctrl.State.Editor.DebugCurrentLine = 5;
        using var registration = ctrl.State.RunCts!.Token.Register(() => throw new InvalidOperationException("cleanup sentinel"));
        finish.TrySetResult(RunResult.Exited(0));
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Error", ctrl.State.ExecStatusText);
        Assert.Contains("cleanup sentinel", ctrl.State.Output.Content);
        Assert.Null(ctrl.State.RunCts);
        Assert.Null(ctrl.State.Editor.DebugCurrentLine);
        Assert.False(ctrl.State.RunStopwatch.IsRunning);
        logger.Verify(l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.Is<Exception>(e => e.ToString().Contains("cleanup sentinel")), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        ((Window)TopLevel.GetTopLevel(ctrl)!).Close();
        ctrl.Dispose();
    }

    [AvaloniaFact]
    public async Task LateOutput_Should_NotReachANewerRun_OrAClosedTab()
    {
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<RunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputs = new List<OutputReceivedHandler>();
        var count = 0;
        var ctrl = CreateControl(RunResult.Exited(0), captureOutput: outputs.Add, pending: () =>
        {
            if (Interlocked.Increment(ref count) == 1) return Task.FromResult(RunResult.Exited(0));
            secondStarted.TrySetResult();
            return finish.Task;
        });
        await ctrl.RunAsync(false, Sdk).WaitAsync(TimeSpan.FromSeconds(10));
        Dispatcher.UIThread.RunJobs();
        var second = ctrl.RunAsync(false, Sdk);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        outputs[0]("stale-run-output", OutputKind.StandardOutput);
        outputs[1]("current-run-output", OutputKind.StandardOutput);
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.DoesNotContain("stale-run-output", ctrl.State.Output.Content);
            Assert.Contains("current-run-output", ctrl.State.Output.Content);
            ((Window)TopLevel.GetTopLevel(ctrl)!).Close();
            ctrl.Dispose();
            var before = ctrl.State.Output.Content;
            outputs[1]("closed-tab-output", OutputKind.StandardOutput);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(before, ctrl.State.Output.Content);
        }
        finally { finish.TrySetResult(RunResult.Exited(0)); }
        await second.WaitAsync(TimeSpan.FromSeconds(10));
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
    public async Task EachStageTheHostReports_Should_BeShownAsTheRunsStatus_AndEndWithTheOutcome()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<RunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<ILanguageHost>? hostMock = null;
        var ctrl = CreateControl(RunResult.Exited(0), configureHost: h => hostMock = h,
            pending: () => { started.TrySetResult(); return finish.Task; });
        var run = ctrl.RunAsync(false, Sdk);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        void Raise(RunStage stage, string? detail) => hostMock!.Raise(h => h.StageChanged += null, stage, detail!);
        Raise(RunStage.RestoringPackages, "2 packages");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Restoring packages (2 packages)…", ctrl.State.ExecStatusText);
        Raise(RunStage.GeneratingConnections, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Generating connections…", ctrl.State.ExecStatusText);
        Raise(RunStage.Compiling, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Compiling…", ctrl.State.ExecStatusText);
        Raise(RunStage.Running, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Process", ctrl.State.ExecStatusText);
        Assert.Equal("RunPad.Status.RunningBrush", ctrl.State.ExecStatusColor);
        // The live output of the stage is not hidden by the status.
        Assert.Contains("script output", ctrl.State.Output.Content);

        finish.TrySetResult(RunResult.Exited(0));
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Success", ctrl.State.ExecStatusText);
    }

    [AvaloniaTheory]
    [InlineData(RunStage.RestoringPackages, "Package restore failed")]
    [InlineData(RunStage.GeneratingConnections, "Connection contract failed")]
    [InlineData(RunStage.Compiling, "Compilation failed")]
    public async Task AFailedCompile_Should_NameTheStageItFailedIn_AndKeepTheDiagnostics(RunStage failedIn, string status)
    {
        var ctrl = CreateControl(RunResult.CompilationFailed, stderrLine: "error NU1101: Unable to find package",
            configureHost: h => h.Setup(x => x.RunAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
                .Returns((string _, OutputReceivedHandler output, CancellationToken _) =>
                {
                    h.Raise(x => x.StageChanged += null, failedIn, null!);
                    output("error NU1101: Unable to find package", OutputKind.StandardError);
                    return Task.FromResult(RunResult.CompilationFailed);
                }));

        await RunAsync(ctrl);

        Assert.Equal(status, ctrl.State.ExecStatusText);
        Assert.Equal("RunPad.Status.ErrorBrush", ctrl.State.ExecStatusColor);
        Assert.Contains("NU1101", ctrl.State.Output.Content);
    }

    [AvaloniaFact]
    public async Task StoppingWhileConnectionSchemasAreRead_Should_CancelTheRun_WithoutStartingAHost()
    {
        var drivers = new ConnectionDriverRegistry();
        var driver = new BlockingSchemaDriver();
        drivers.Register(driver);
        var folder = Directory.CreateTempSubdirectory("runpad-stage-schema").FullName;
        try
        {
            var schemas = new ConnectionSchemas(drivers, folder,
                new ConnectionTools { GetPackageFolderAsync = (_, _, _) => Task.FromResult("") }, _ => null);
            var created = new List<(string Language, ExecutionRequest Request)>();
            var ctrl = CreateControl(RunResult.Exited(0), created: created, connectionSchemas: schemas, connectionDrivers: drivers);
            ctrl.State.RpadDocument = new RpadDocument
            {
                FrontMatter = new RpadFrontMatter
                {
                    Connections = [new RpadConnection { Name = "slow", Driver = "blocking" }],
                },
                RunCode = "",
            };
            var run = ctrl.RunAsync(false, Sdk);
            await driver.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Reading connection schemas…", ctrl.State.ExecStatusText);

            ctrl.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Cancelled", ctrl.State.ExecStatusText);
            Assert.Empty(created);
            Assert.Null(ctrl.State.RunCts);
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class BlockingSchemaDriver : IConnectionSchemaDriver
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "blocking";
        public string DisplayName => "Blocking";
        public ConnectionCategory Category => ConnectionCategory.Grpc;
        public IReadOnlyList<ConnectionParam> Params { get; } = [];
        public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string>();
        public IReadOnlyList<string> Validate(RpadConnection connection) => [];
        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(true, ""));
        public ConnectionClientCode CreateClient(RpadConnection connection, ConnectionCodeContext context) =>
            new($"public sealed class {context.ClassName} {{ }}");
        public async Task<ConnectionSchema> FetchSchemaAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, ConnectionTools tools,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ConnectionSchema("", [], "");
        }
    }

    [AvaloniaFact]
    public async Task AStartFailure_Should_FailTheRun_WithAMessage()
    {
        var ctrl = CreateControl(RunResult.StartFailed);

        await RunAsync(ctrl);

        Assert.Equal("Error", ctrl.State.ExecStatusText);
        Assert.Contains("Failed to start the compiled script.", ctrl.State.Output.Content);
    }

    // The tab asks its language for a host with a plain request — the SDK, where it builds, its
    // document's defaults (an untitled tab has no front matter yet), the user's dotnet and debugger and
    // exception choices — never with the backend's own options, for C# and Visual Basic alike.
    [AvaloniaTheory]
    [InlineData(RpadLanguage.CSharp, "csharp")]
    [InlineData(RpadLanguage.Vb, "vb")]
    public async Task TheRun_Should_AskItsLanguage_WithThePlainRequest(RpadLanguage language, string languageId)
    {
        var created = new List<(string Language, ExecutionRequest Request)>();
        var ctrl = CreateControl(RunResult.Exited(0), language: language, created: created);

        await RunAsync(ctrl);

        var (id, request) = Assert.Single(created);
        Assert.Equal(languageId, id);
        Assert.Same(Sdk, request.Platform);
        Assert.Equal(ctrl.State.OutputDirectory, request.OutputDirectory);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "RunPad"), request.WorkingDirectory);
        Assert.Null(request.CurrentQueryPath);
        Assert.Equal(RpadKind.Statements, request.Kind);
        Assert.Empty(request.References);
        Assert.Empty(request.Arguments);
        Assert.Equal(new DebuggerSettings().DotnetPath, request.RuntimePath);
        Assert.Equal(new DebuggerSettings().DebuggerPath, request.DebuggerPath);
        Assert.False(request.BreakOnAllExceptions);
        Assert.True(request.BreakOnUncaughtExceptions);
    }

    [Fact]
    public void TheShippedLocator_Should_FindTheRunPadRuntimeAssembly()
    {
        var path = new DotnetRuntimeReferenceLocator().GetRuntimeAssemblyPath();

        Assert.Equal("RunPad.Runtime.dll", Path.GetFileName(path));
        Assert.True(File.Exists(path), path);
        // The C# editor's IntelliSense references the very same assembly the run compiles against.
        Assert.Equal(path, new CSharpReferenceService().GetExtensionReference());
    }

}
