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

// RunAsync/RunDebugAsync used to `using var cts = ...` their linked CTS — disposed
// at method exit, right after their own `finally { _runCts = null; }`. TerminateAsync's
// `_runCts?.Cancel()` runs on a different thread (a "Stop" click), so a read of the (still non-null)
// field followed by a context switch could land its `.Cancel()` call on a CTS the run's own method
// had, in the meantime, already disposed — ObjectDisposedException. Fixed by never disposing that
// CTS at all (same "Cancel() now, don't Dispose()" pattern already used for three other
// CTS Cancel+Dispose races) — these tests exercise TerminateAsync racing a genuinely in-flight run,
// which had zero coverage before this fix.
public class CompiledDotnetLanguageHostTerminateAsyncTests
{
    private sealed class FakeCompiler(ProcessCompilerResult result) : IDotnetProcessCompiler
    {
        public Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx) => Task.FromResult(result);

        public void Dispose()
        {
        }
    }

    // Never reaches EOF on its own — DrainReaderAsync's ReadLineAsync(ct) stays pending until the
    // token it was given is cancelled, keeping RunAsync genuinely in-flight (not already completed)
    // for as long as the test needs to race TerminateAsync against it.
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeChildProcess : IChildProcess
    {
        public StreamReader StandardOutput { get; } = new(new BlockingStream());
        public StreamReader StandardError { get; } = new(new MemoryStream());
        public Stream OutputStream { get; } = new MemoryStream();
        public Stream InputStream { get; } = new MemoryStream();

        public bool Start() => true;

        public void Kill()
        {
        }

        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
        public int ExitCode => 0;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (CSharpLanguageHost Host, string OutputDir) CreateHost()
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

        var compiler = new FakeCompiler(new ProcessCompilerResult("out.dll", true, [], []));
        var host = new CSharpLanguageHost(
            options, NullLoggerFactory.Instance, (_, _, _) => compiler,
            executionProcessFactory: (_, _, _, _) => new FakeChildProcess());
        return (host, outputDir);
    }

    [Fact]
    public async Task TerminateAsync_Should_NotThrow_WhenARunIsGenuinelyInFlight()
    {
        var (host, outputDir) = CreateHost();
        try
        {
            var runTask = host.RunAsync("int x = 1;", cancellationToken: TestContext.Current.CancellationToken);

            // Let RunAsync actually reach its blocking read before racing Terminate against it —
            // otherwise this would trivially pass without ever exercising an in-flight run.
            for (var i = 0; i < 100 && runTask.IsCompleted; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);

            await host.TerminateAsync(TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task TerminateAsync_Should_NotThrow_WhenCalledAfterTheRunAlreadyCompleted()
    {
        var (host, outputDir) = CreateHost();
        try
        {
            var runTask = host.RunAsync("int x = 1;", cancellationToken: TestContext.Current.CancellationToken);
            for (var i = 0; i < 100 && runTask.IsCompleted; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);

            await host.TerminateAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

            // A "late" Terminate arriving after the run has already unwound — this is exactly the
            // shape of call that used to race a disposed CTS under the old `using var cts = ...`.
            await host.TerminateAsync(TestContext.Current.CancellationToken);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
