using Invoklet.Build.Core.Models;
using Invoklet.Build.Core.ProcessRunners;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// RunAsync/RunDebugAsync used to always ChildProcess.ForExecution(...)/
// ForDebugger(...) directly, so exercising them meant either shelling out to a real dotnet/netcoredbg
// process or not testing them at all — the same gap CompiledDotnetLanguageHostCompilerFactoryTests
// already closed for the compiler seam. The injected ExecutionChildProcessFactory/
// DebuggerChildProcessFactory let a fake process stand in instead.
public class CompiledDotnetLanguageHostProcessFactoryTests
{
    private sealed class FakeCompiler(ProcessCompilerResult result) : IDotnetProcessCompiler
    {
        public Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx) => Task.FromResult(result);

        public void Dispose()
        {
        }
    }

    // Empty in-memory streams are enough here: DrainReaderAsync's ReadLineAsync (Run) and
    // DapSession's ReadMessageAsync (RunDebug) both treat an immediate EOF as "nothing more to read"
    // and return cleanly — no real dotnet/netcoredbg process, no protocol scripting needed.
    private sealed class FakeChildProcess : IChildProcess
    {
        public bool Started { get; private set; }

        public StreamReader StandardOutput { get; } = new(new MemoryStream());
        public StreamReader StandardError { get; } = new(new MemoryStream());
        public Stream OutputStream { get; } = new MemoryStream();
        public Stream InputStream { get; } = new MemoryStream();

        public bool Start()
        {
            Started = true;
            return true;
        }

        public void Kill()
        {
        }

        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
        public int ExitCode => 0;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (CSharpLanguageHost Host, string OutputDir) CreateHost(
        ExecutionChildProcessFactory? executionProcessFactory = null,
        DebuggerChildProcessFactory? debuggerProcessFactory = null,
        string? currentQueryPath = null,
        IReadOnlyList<string>? arguments = null)
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "Invoklet-tests-" + Guid.NewGuid());
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
            RuntimeLibPath = "Invoklet.Runtime.dll",
            Kind = MdRunKind.Statements,
            CurrentQueryPath = currentQueryPath,
            Arguments = arguments ?? [],
        };

        var compiler = new FakeCompiler(new ProcessCompilerResult("out.dll", true, [], []));
        var host = new CSharpLanguageHost(
            options, NullLoggerFactory.Instance, (_, _, _) => compiler, executionProcessFactory, debuggerProcessFactory);
        return (host, outputDir);
    }

    [Fact]
    public async Task RunAsync_Should_UseTheInjectedExecutionProcess_InsteadOfShellingOutToDotnet()
    {
        FakeChildProcess? received = null;
        string? receivedAssemblyPath = null;
        ExecutionChildProcessFactory factory = (options, assemblyPath, _, _, _) =>
        {
            receivedAssemblyPath = assemblyPath;
            return received = new FakeChildProcess();
        };

        var (host, outputDir) = CreateHost(executionProcessFactory: factory);
        try
        {
            await host.RunAsync("int x = 1;", cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotNull(received);
            Assert.True(received.Started);
            Assert.Equal("out.dll", receivedAssemblyPath);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // Confirms CompiledDotnetLanguageHostBase.RunAsync
    // actually forwards Options.CurrentQueryPath into the process factory (ChildProcess.ForExecution's
    // own handling of it is tested separately in ChildProcessCurrentQueryPathTests.cs, without needing
    // a whole host/compile pipeline).
    [Fact]
    public async Task RunAsync_Should_PassCurrentQueryPath_ToTheExecutionProcessFactory()
    {
        string? received = null;
        ExecutionChildProcessFactory factory = (_, _, _, currentQueryPath, _) =>
        {
            received = currentQueryPath;
            return new FakeChildProcess();
        };

        var (host, outputDir) = CreateHost(executionProcessFactory: factory, currentQueryPath: "/root/a.inv");
        try
        {
            await host.RunAsync("int x = 1;", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("/root/a.inv", received);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_Should_PassTheScriptArguments_ToTheExecutionProcessFactory()
    {
        IReadOnlyList<string>? received = null;
        ExecutionChildProcessFactory factory = (_, _, _, _, arguments) =>
        {
            received = arguments;
            return new FakeChildProcess();
        };

        var (host, outputDir) = CreateHost(executionProcessFactory: factory, arguments: ["a", "b c"]);
        try
        {
            await host.RunAsync("int x = 1;", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(["a", "b c"], received);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task RunDebugAsync_Should_UseTheInjectedDebuggerProcess_InsteadOfShellingOutToNetcoredbg()
    {
        FakeChildProcess? received = null;
        DebuggerChildProcessFactory factory = _ => received = new FakeChildProcess();

        var (host, outputDir) = CreateHost(debuggerProcessFactory: factory);
        try
        {
            await host.RunDebugAsync(
                "int x = 1;", outputHandler: static (_, _) => { }, TestContext.Current.CancellationToken);

            Assert.NotNull(received);
            Assert.True(received.Started);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // A blocking pipe, not FakeChildProcess's plain MemoryStream (which returns EOF once its
    // buffer's exhausted) — needed here so DapSession's real read loop stays genuinely alive long
    // enough to receive netcoredbg's "initialize" response and send the launch request back, instead
    // of seeing immediate EOF and exiting before ever writing anything (same rationale as
    // DapSessionRunningBreakpointResyncTests/DapSessionCurrentQueryPathTests' own PipeStream —
    // duplicated here too, all three files are small and self-contained).
    private sealed class PipeStream : Stream
    {
        private readonly System.Threading.Channels.Channel<byte> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<byte>();

        public void Push(byte[] bytes)
        {
            foreach (var b in bytes) _channel.Writer.TryWrite(b);
        }

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
            if (!await _channel.Reader.WaitToReadAsync(cancellationToken)) return 0;
            return _channel.Reader.TryRead(out var b) ? Fill(buffer, b) : 0;
        }

        private static int Fill(Memory<byte> buffer, byte b)
        {
            buffer.Span[0] = b;
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PipeFakeChildProcess : IChildProcess
    {
        public PipeStream Output { get; } = new();
        public MemoryStream Input { get; } = new();

        public StreamReader StandardOutput { get; } = new(new MemoryStream());
        public StreamReader StandardError { get; } = new(new MemoryStream());
        public Stream OutputStream => Output;
        public Stream InputStream => Input;

        public bool Start() => true;
        public void Kill() { }
        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
        public int ExitCode => 0;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static byte[] Frame(object body)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        var bodyBytes = System.Text.Encoding.UTF8.GetBytes(json);
        var header = System.Text.Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
        return [.. header, .. bodyBytes];
    }

    private static async Task<string> WaitForWrittenRequestAsync(MemoryStream input, string containing)
    {
        for (var i = 0; i < 100; i++)
        {
            var written = System.Text.Encoding.UTF8.GetString(input.ToArray());
            if (written.Contains(containing)) return written;
            await Task.Delay(20);
        }

        throw new TimeoutException($"""Never saw a written request containing "{containing}".""");
    }

    // Live user report ("Util.CurrentQueryPath doesn't work while debugging"): confirms
    // the full RunDebugAsync path, not just DapSession in isolation (DapSessionCurrentQueryPathTests.cs
    // already covers that half) — Options.CurrentQueryPath really does reach the real DapSession this
    // host constructs internally, which really does put it in the actual launch request.
    [Fact]
    public async Task RunDebugAsync_Should_ForwardCurrentQueryPath_ToTheRealLaunchRequest()
    {
        var process = new PipeFakeChildProcess();
        DebuggerChildProcessFactory factory = _ => process;

        var (host, outputDir) = CreateHost(debuggerProcessFactory: factory, currentQueryPath: "/root/a.inv");
        try
        {
            var runTask = host.RunDebugAsync(
                "int x = 1;", outputHandler: static (_, _) => { }, TestContext.Current.CancellationToken);

            process.Output.Push(Frame(new { seq = 1, type = "response", request_seq = 1, command = "initialize", success = true }));
            var written = await WaitForWrittenRequestAsync(process.Input, "\"command\":\"launch\"");

            Assert.Contains("INVOKLET_CURRENT_QUERY_PATH", written);
            Assert.Contains("/root/a.inv", written);

            process.Output.Push(Frame(new { seq = 2, type = "event", @event = "exited" }));
            await runTask;
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // The debug launch must use the same configured dotnet host
    // as the non-debug run, not netcoredbg's own PATH lookup of "dotnet".
    [Fact]
    public async Task RunDebugAsync_Should_LaunchThroughTheConfiguredRuntimePath()
    {
        var process = new PipeFakeChildProcess();
        DebuggerChildProcessFactory factory = _ => process;

        var (host, outputDir) = CreateHost(debuggerProcessFactory: factory);
        try
        {
            var runTask = host.RunDebugAsync(
                "int x = 1;", outputHandler: static (_, _) => { }, TestContext.Current.CancellationToken);

            process.Output.Push(Frame(new { seq = 1, type = "response", request_seq = 1, command = "initialize", success = true }));
            var written = await WaitForWrittenRequestAsync(process.Input, "\"command\":\"launch\"");

            Assert.Contains("\"program\":\"dotnet\"", written);
            Assert.Contains("\"args\":[\"out.dll\"]", written);

            process.Output.Push(Frame(new { seq = 2, type = "event", @event = "exited" }));
            await runTask;
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
