using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Build.Core.ProcessRunners;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Compiler;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// RunAsync used to wait only for stdout/stderr EOF,
// then dispose (kill) the process and never read its exit code — `Environment.Exit(7)` looked like a
// success, and a script that closed its own output streams was killed while still running. Now the
// run completes when the process exits and reports how it ended (RunResult).
[Trait("Category", "Integration")]
[Trait("Category", "RealBuild")]
public class CompiledDotnetLanguageHostRunResultTests
{
    private sealed class FakeCompiler(ProcessCompilerResult result) : IDotnetProcessCompiler
    {
        public Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx) => Task.FromResult(result);

        public void Dispose()
        {
        }
    }

    // stdout/stderr are fixed text that hits EOF once read; the process "exits" only when the test
    // calls Exit — so output closing and the process exiting can happen at different times.
    private sealed class FakeChildProcess(string stdout = "", string stderr = "", bool starts = true) : IChildProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StreamReader StandardOutput { get; } = new(new MemoryStream(Encoding.UTF8.GetBytes(stdout)));
        public StreamReader StandardError { get; } = new(new MemoryStream(Encoding.UTF8.GetBytes(stderr)));
        public Stream OutputStream { get; } = new MemoryStream();
        public Stream InputStream { get; } = new MemoryStream();

        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }

        public void Exit(int code) => _exit.TrySetResult(code);

        public bool Start() => starts;

        public void Kill() => Killed = true;

        public Task WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

        public int ExitCode => _exit.Task.Result;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Kill();
            return ValueTask.CompletedTask;
        }
    }

    private static (CSharpLanguageHost Host, string OutputDir) CreateHost(
        FakeChildProcess process, bool compiles = true, Action? onProcessCreated = null)
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "runpad-tests-" + Guid.NewGuid());
        var options = new CompiledDotnetLanguageHostOptions
        {
            Platform = new DotnetSdk("net10.0", "net10.0", "10.0.0", "net10.0"),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = outputDir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "netcoredbg" },
            },
            OutputDirectory = outputDir,
            RuntimeLibPath = "RunPad.Runtime.dll",
            Kind = RpadKind.Statements,
        };

        var compiler = new FakeCompiler(new ProcessCompilerResult("out.dll", compiles, [], []));
        var host = new CSharpLanguageHost(
            options, NullLoggerFactory.Instance, (_, _, _) => compiler,
            (_, _, _, _, _) =>
            {
                onProcessCreated?.Invoke();
                return process;
            });
        return (host, outputDir);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(-532462766)] // an unhandled exception's exit code on Windows (0xE0434352)
    public async Task RunAsync_Should_ReportTheProcesssExitCode(int exitCode)
    {
        var process = new FakeChildProcess(stdout: "hello\n");
        process.Exit(exitCode);
        var (host, outputDir) = CreateHost(process);
        try
        {
            var output = new List<string>();
            var result = await host.RunAsync("x", (data, kind) => output.Add($"{kind}: {data}"),
                TestContext.Current.CancellationToken);

            Assert.Equal(RunResult.Exited(exitCode), result);
            Assert.Equal(exitCode == 0, result.Succeeded);
            Assert.Equal(["StandardOutput: hello"], output);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_Should_NotFailARun_JustBecauseItWroteToStderr()
    {
        var process = new FakeChildProcess(stderr: "warning: something informational\n");
        process.Exit(0);
        var (host, outputDir) = CreateHost(process);
        try
        {
            var output = new List<string>();
            var result = await host.RunAsync("x", (data, kind) => output.Add($"{kind}: {data}"),
                TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded);
            Assert.Equal(["StandardError: warning: something informational"], output);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // A script that closes stdout/stderr (EOF on both pipes) keeps running — the run must wait for
    // the process to exit, not dispose (kill) it as soon as its output ends.
    [Fact]
    public async Task RunAsync_Should_WaitForTheProcessToExit_AfterItsOutputHasClosed()
    {
        var process = new FakeChildProcess(stdout: "before close\n");
        var (host, outputDir) = CreateHost(process);
        try
        {
            var outputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = host.RunAsync("x", (_, _) => outputClosed.TrySetResult(),
                TestContext.Current.CancellationToken);

            await outputClosed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(run.IsCompleted);
            Assert.False(process.Killed);

            process.Exit(3);
            Assert.Equal(RunResult.Exited(3), await run);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_Should_ReportACompilationFailure_WithoutStartingAProcess()
    {
        var created = false;
        var (host, outputDir) = CreateHost(new FakeChildProcess(), compiles: false, onProcessCreated: () => created = true);
        try
        {
            var result = await host.RunAsync("x", static (_, _) => { }, TestContext.Current.CancellationToken);

            Assert.Equal(RunResult.CompilationFailed, result);
            Assert.False(result.Succeeded);
            Assert.False(created);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_Should_ReportAProcessThatFailedToStart()
    {
        var (host, outputDir) = CreateHost(new FakeChildProcess(starts: false));
        try
        {
            var result = await host.RunAsync("x", static (_, _) => { }, TestContext.Current.CancellationToken);

            Assert.Equal(RunResult.StartFailed, result);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // Stop still works while the script runs on after closing its output: the wait for exit is
    // cancelled and the process is killed by the disposal.
    [Fact]
    public async Task TerminateAsync_Should_StopARunThatIsWaitingForTheProcessToExit()
    {
        var process = new FakeChildProcess();
        var (host, outputDir) = CreateHost(process);
        try
        {
            var run = host.RunAsync("x", static (_, _) => { }, TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(run.IsCompleted);

            await host.TerminateAsync(TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(process.Killed);
            Assert.True(process.Disposed);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
