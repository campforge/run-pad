using System.Reactive.Linq;
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
using RunPad.Languages.DotnetCommon;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Run and Debug save the script first. That save used to write the file unconditionally — replacing
// what another program had written meanwhile — and didn't record what it wrote, so the next normal
// save took RunPad's own write for someone else's. Now it is the same guarded save as Ctrl+S: a
// changed file is only replaced on the user's choice, and when the save doesn't happen the script
// doesn't run.
public class RunSaveConflictTests : IDisposable
{
    private static readonly DotnetSdk Sdk = new("10.0.100", "net10.0", "10.0.0", ".NET 10.0");
    private const string External = "Console.WriteLine(\"external\");";

    private readonly string _root = Directory.CreateTempSubdirectory("runpad-run-save").FullName;
    private readonly string _script;
    private readonly Mock<IWindowService> _windows = new();
    private readonly List<ExecutionRequest> _runs = [];
    private readonly List<string> _scripts = [];
    private Func<CancellationToken, Task<RunResult>>? _execute;
    private Func<ValueTask>? _disposeHost;

    public RunSaveConflictTests()
    {
        _script = Path.Combine(_root, "query.rpad");
        File.WriteAllText(_script, RpadWriter.NewFile("Console.WriteLine(\"original\");\n", "Demo"));
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
            foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.Delete(_root, recursive: true);
    }

    private (EditorsTabItemControl Ctrl, EditorTabItemViewModel Vm, Window Host) Open()
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
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(_root);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var shell = new MainWindowViewModel([], fileSystem.Object, _windows.Object, prefs.Object, new LanguageHostRegistry());

        var host = new Mock<ILanguageHost>();
        host.Setup(h => h.RunAsync(It.IsAny<string>(), It.IsAny<OutputReceivedHandler>(), It.IsAny<CancellationToken>()))
            .Returns((string code, OutputReceivedHandler output, CancellationToken token) =>
            {
                _scripts.Add(code);
                return _execute?.Invoke(token) ?? Task.FromResult(RunResult.Exited(0));
            });
        host.Setup(h => h.DisposeAsync()).Returns(() => _disposeHost?.Invoke() ?? ValueTask.CompletedTask);
        var registry = new Mock<ILanguageHostRegistry>();
        registry.Setup(r => r.Create(It.IsAny<string>(), It.IsAny<ExecutionRequest>()))
            .Callback<string, ExecutionRequest>((_, request) => _runs.Add(request)).Returns(host.Object);
        var buildCache = new Mock<IBuildCacheService>();
        buildCache.Setup(b => b.EnsureOutputDirectory(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(() => Directory.CreateDirectory(Path.Combine(_root, "build")).FullName);

        var vm = new EditorTabItemViewModel(_script, [Sdk], prefs.Object, shell, RpadLanguage.CSharp, RpadKind.Statements);
        var ctrl = new EditorsTabItemControl(vm, theme.Object, _windows.Object, registry.Object, prefs.Object,
            intelliSense: null, buildCache.Object);
        var window = new Window { Content = ctrl };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (ctrl, vm, window);
    }

    [AvaloniaFact]
    public async Task Selection_Should_RunOnlySelectedStatements_WithoutSavingTheDocument()
    {
        var original = File.ReadAllBytes(_script);
        var (ctrl, vm, window) = Open();
        try
        {
            ctrl.State.Editor.Text = "throw new Exception();\n42.Dump();\nthrow new Exception();";
            ctrl.State.RpadDocument!.FrontMatter.Args = ["argument"];
            ctrl.State.Editor.Select(ctrl.State.Editor.Text.IndexOf("42.Dump", StringComparison.Ordinal), 10);
            await ctrl.RunSelectionAsync(Sdk);
            var script = Assert.Single(_scripts);
            Assert.Equal("42.Dump();", script.Trim());
            Assert.Equal(1, script.Count(c => c == '\n'));
            Assert.Equal(["argument"], Assert.Single(_runs).Arguments);
            Assert.Equal(original, File.ReadAllBytes(_script));
            Assert.True(vm.IsDirty);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public async Task EmptySelection_Should_NotRunTheWholeDocument()
    {
        var (ctrl, _, window) = Open();
        try { await ctrl.RunSelectionAsync(Sdk); Assert.Empty(_runs); }
        finally { window.Close(); ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Restart_Should_WaitForCancelledHostDisposal_AndStartExactlyOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalCalls = 0;
        _disposeHost = () =>
        {
            if (Interlocked.Increment(ref disposalCalls) != 1) return ValueTask.CompletedTask;
            disposing.SetResult();
            return new ValueTask(disposed.Task);
        };
        var calls = 0;
        _execute = async token =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.SetResult();
                await finish.Task;
                token.ThrowIfCancellationRequested();
            }
            return RunResult.Exited(0);
        };
        var (ctrl, _, window) = Open();
        try
        {
            var first = ctrl.RunAsync(false, Sdk);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var restart = ctrl.RestartAsync(Sdk);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ctrl.RestartAsync(Sdk);
            Assert.Single(_runs);
            finish.SetResult();
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(_runs);
            disposed.SetResult();
            await first;
            await restart;
            Assert.Equal(2, _runs.Count);
        }
        finally { finish.TrySetResult(); disposed.TrySetResult(); window.Close(); ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public async Task StopDuringQueuedRestart_Should_NotLaunchAnotherHost()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _execute = async token => { entered.TrySetResult(); await finish.Task; token.ThrowIfCancellationRequested(); return RunResult.Exited(0); };
        var (ctrl, _, window) = Open();
        try
        {
            var first = ctrl.RunAsync(false, Sdk);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var restart = ctrl.RestartAsync(Sdk);
            ctrl.Cancel();
            finish.SetResult();
            await first;
            await restart;
            Assert.Single(_runs);
        }
        finally { finish.TrySetResult(); window.Close(); ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public async Task ProgramSelection_Should_NotRunTheWholeProgram()
    {
        var (ctrl, _, window) = Open();
        try
        {
            ctrl.State.RpadDocument!.FrontMatter.Kind = RpadKind.Program;
            ctrl.State.Editor.Select(0, 5);
            await ctrl.RunSelectionAsync(Sdk);
            Assert.Empty(_runs);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartDuringSaveQuestion_Should_StartOnlyTheNewRun(bool stopQueuedRestart)
    {
        var (ctrl, _, window) = Open();
        try
        {
            ChangeExternally();
            var answer = new TaskCompletionSource<SaveConflictChoice>();
            _windows.Setup(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>())).Returns(answer.Task);
            var first = ctrl.RunAsync(false, Sdk);
            var restart = ctrl.RestartAsync(Sdk);
            if (stopQueuedRestart) ctrl.Cancel();
            Assert.Empty(_runs);
            answer.SetResult(SaveConflictChoice.Overwrite);
            await first;
            await restart;
            Assert.Equal(stopQueuedRestart ? 0 : 1, _runs.Count);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    private void ChangeExternally() => File.WriteAllText(_script, RpadWriter.NewFile(External + "\n", "Demo"));

    private void Answer(SaveConflictChoice choice) =>
        _windows.Setup(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>())).ReturnsAsync(choice);

    private static string? CodeIn(string path) => RpadParser.TryParse(File.ReadAllText(path))?.RunCode.Trim();

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunOrDebug_AfterAnExternalChange_WithCancel_Should_NotRun_AndKeepTheFile(bool debug)
    {
        var (ctrl, vm, window) = Open();
        try
        {
            ChangeExternally();
            var changed = File.ReadAllBytes(_script);
            Answer(SaveConflictChoice.Cancel);

            await ctrl.RunAsync(debug, Sdk);

            Assert.Empty(_runs);
            Assert.Equal(changed, File.ReadAllBytes(_script));
            Assert.True(vm.IsDirty);
            Assert.Equal(RunState.Idle, vm.RunState);
            Assert.StartsWith("Not saved", ctrl.State.ExecStatusText, StringComparison.Ordinal);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Run_WithOverwrite_Should_SaveThenRun_AndTheNextSave_Should_NotAsk()
    {
        var (ctrl, vm, window) = Open();
        try
        {
            ChangeExternally();
            Answer(SaveConflictChoice.Overwrite);

            await ctrl.RunAsync(debug: false, Sdk);

            Assert.Single(_runs);
            Assert.Equal("Console.WriteLine(\"original\");", CodeIn(_script));
            _windows.Verify(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>()), Times.Once);

            ctrl.State.Editor.Text = "Console.WriteLine(2);\n";
            Dispatcher.UIThread.RunJobs();
            ctrl.SaveIfNeeded();

            _windows.Verify(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>()), Times.Once);
            Assert.Equal("Console.WriteLine(2);", CodeIn(_script));
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    // Without any change by another program: no question, the run happens, and what Run wrote is the
    // tab's baseline — the next save doesn't mistake it for another program's change.
    [AvaloniaFact]
    public async Task Run_WithoutAnExternalChange_Should_RunWithoutAsking_AndAcceptItsOwnWrite()
    {
        var (ctrl, vm, window) = Open();
        try
        {
            ctrl.State.Editor.Text = "Console.WriteLine(1);\n";
            Dispatcher.UIThread.RunJobs();

            await ctrl.RunAsync(debug: false, Sdk);
            ctrl.State.Editor.Text = "Console.WriteLine(2);\n";
            Dispatcher.UIThread.RunJobs();
            ctrl.SaveIfNeeded();

            Assert.Single(_runs);
            _windows.Verify(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>()), Times.Never);
            Assert.Equal("Console.WriteLine(2);", CodeIn(_script));
            Assert.False(vm.IsDirty);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Run_WithSaveAs_Should_RunTheScriptFromItsNewFile()
    {
        var (ctrl, vm, window) = Open();
        try
        {
            ChangeExternally();
            Answer(SaveConflictChoice.SaveAs);
            var other = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "other")).FullName, "query.rpad");
            _windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(other);

            await ctrl.RunAsync(debug: false, Sdk);

            Assert.Equal(other, vm.FilePath);
            Assert.Equal(other, Assert.Single(_runs).CurrentQueryPath);
            Assert.Equal(External, CodeIn(_script));
            Assert.False(vm.IsDirty);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Run_WithSaveAs_AndACancelledPicker_Should_NotRun()
    {
        var (ctrl, vm, window) = Open();
        try
        {
            ChangeExternally();
            Answer(SaveConflictChoice.SaveAs);
            _windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((string?)null);

            await ctrl.RunAsync(debug: false, Sdk);

            Assert.Empty(_runs);
            Assert.Equal(External, CodeIn(_script));
            Assert.Equal(_script, vm.FilePath);
            Assert.True(vm.IsDirty);
            Assert.Equal(RunState.Idle, vm.RunState);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Run_WithAFailedOverwrite_Should_NotRun_AndSayWhy()
    {
        if (OperatingSystem.IsWindows()) return; // read-only through Unix file modes
        var (ctrl, vm, window) = Open();
        try
        {
            ChangeExternally();
            var changed = File.ReadAllBytes(_script);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_script, UnixFileMode.UserRead);
            Answer(SaveConflictChoice.Overwrite);

            await ctrl.RunAsync(debug: false, Sdk);

            Assert.Empty(_runs);
            Assert.Equal(changed, File.ReadAllBytes(_script));
            Assert.True(vm.IsDirty);
            Assert.Equal(RunState.Idle, vm.RunState);
            Assert.Contains("Cannot save 'query.rpad'", System.Net.WebUtility.HtmlDecode(ctrl.State.Output.Content));
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    // While the question is open, another Run starts nothing; one answer, one run.
    [AvaloniaFact]
    public async Task ASecondRun_WhileTheQuestionIsOpen_Should_DoNothing()
    {
        var (ctrl, _, window) = Open();
        try
        {
            ChangeExternally();
            var answer = new TaskCompletionSource<SaveConflictChoice>();
            _windows.Setup(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>())).Returns(answer.Task);

            var first = ctrl.RunAsync(debug: false, Sdk);
            await ctrl.RunAsync(debug: false, Sdk);
            answer.SetResult(SaveConflictChoice.Overwrite);
            await first;

            Assert.Single(_runs);
            _windows.Verify(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>()), Times.Once);
        }
        finally { window.Close(); ctrl.Dispose(); }
    }

    // An untitled script still runs without being saved.
    [AvaloniaFact]
    public async Task AnUntitledScript_Should_RunWithoutASave()
    {
        File.Delete(_script);
        var (ctrl, vm, window) = Open();
        try
        {
            vm.FilePath = null;
            await ctrl.RunAsync(debug: false, Sdk);

            Assert.Single(_runs);
            _windows.Verify(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>()), Times.Never);
            Assert.False(File.Exists(_script));
        }
        finally { window.Close(); ctrl.Dispose(); }
    }
}
