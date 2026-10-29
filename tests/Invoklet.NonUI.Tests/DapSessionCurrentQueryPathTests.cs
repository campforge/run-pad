using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Invoklet.Build.Core.ProcessRunners;
using Invoklet.Debug;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Live user report ("Util.CurrentQueryPath doesn't work while debugging, but all
// features should work while debugging"): a debug session launches the debuggee through netcoredbg (a
// separate process Invoklet doesn't control the environment of), not directly like a plain run, so
// forwarding CurrentQueryPath needed its own path — the DAP launch request's own `env` field, which
// netcoredbg already supports but DapSession never populated. Same fake-netcoredbg-conversation
// technique as DapSessionRunningBreakpointResyncTests.cs (PipeStream/Frame/WaitForWrittenRequestAsync
// — duplicated here rather than shared, both files are already small and self-contained).
public class DapSessionCurrentQueryPathTests
{
    private sealed class PipeStream : Stream
    {
        private readonly Channel<byte> _channel = Channel.CreateUnbounded<byte>();

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

    private sealed class FakeChildProcess : IChildProcess
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
        var json = JsonSerializer.Serialize(body);
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
        return [.. header, .. bodyBytes];
    }

    private static async Task<string> WaitForWrittenRequestAsync(MemoryStream input, string containing)
    {
        for (var i = 0; i < 100; i++)
        {
            var written = Encoding.UTF8.GetString(input.ToArray());
            if (written.Contains(containing)) return written;
            await Task.Delay(20);
        }

        throw new TimeoutException($"""Never saw a written request containing "{containing}".""");
    }

    [Fact]
    public async Task RunAsync_Should_SendCurrentQueryPath_InTheLaunchRequestsEnv()
    {
        var process = new FakeChildProcess();
        var options = new DapSessionOptions(
            ProgramPath: "out.dll",
            WorkingDirectory: ".",
            Breakpoints: [],
            SourceFileExtension: ".cs",
            LineOffset: 0,
            CurrentQueryPath: "/root/a.rpad");

        await using var session = new DapSession(
            process, options, outputHandler: null, onBreakpointHit: static _ => { },
            logger: NullLogger<DapSession>.Instance);

        var runTask = session.RunAsync(TestContext.Current.CancellationToken);

        process.Output.Push(Frame(new { seq = 1, type = "response", request_seq = 1, command = "initialize", success = true }));
        var written = await WaitForWrittenRequestAsync(process.Input, "\"command\":\"launch\"");

        Assert.Contains("Invoklet_CURRENT_QUERY_PATH", written);
        Assert.Contains("/root/a.rpad", written);

        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "exited" }));
        await runTask;
    }

    [Fact]
    public async Task RunAsync_Should_OmitEnv_WhenCurrentQueryPathIsNull()
    {
        var process = new FakeChildProcess();
        var options = new DapSessionOptions(
            ProgramPath: "out.dll",
            WorkingDirectory: ".",
            Breakpoints: [],
            SourceFileExtension: ".cs",
            LineOffset: 0,
            CurrentQueryPath: null);

        await using var session = new DapSession(
            process, options, outputHandler: null, onBreakpointHit: static _ => { },
            logger: NullLogger<DapSession>.Instance);

        var runTask = session.RunAsync(TestContext.Current.CancellationToken);

        process.Output.Push(Frame(new { seq = 1, type = "response", request_seq = 1, command = "initialize", success = true }));
        var written = await WaitForWrittenRequestAsync(process.Input, "\"command\":\"launch\"");

        Assert.DoesNotContain("Invoklet_CURRENT_QUERY_PATH", written);
        Assert.DoesNotContain("\"env\"", written);

        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "exited" }));
        await runTask;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/root/a.rpad")]
    public async Task RunAsync_Should_ForwardTypedArguments_InTheLaunchEnvironment(string? path)
    {
        const string contract = "{\"Members\":[{\"Name\":\"value\",\"Type\":\"int\",\"Values\":[\"3\"]}]}";
        var process = new FakeChildProcess();
        await using var session = new DapSession(process,
            new DapSessionOptions("out.dll", ".", [], ".cs", 0, CurrentQueryPath: path,
                Arguments: ["--value", "3"], ArgumentContractJson: contract),
            null, static _ => { }, NullLogger<DapSession>.Instance);
        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        process.Output.Push(Frame(new { seq = 1, type = "response", request_seq = 1, command = "initialize", success = true }));
        var written = await WaitForWrittenRequestAsync(process.Input, "\"command\":\"launch\"");
        var start = written.LastIndexOf("Content-Length:", StringComparison.Ordinal);
        var body = written[(written.IndexOf("\r\n\r\n", start, StringComparison.Ordinal) + 4)..];
        using var json = JsonDocument.Parse(body);
        var launch = json.RootElement.GetProperty("arguments");
        Assert.Equal(contract, launch.GetProperty("env").GetProperty("Invoklet_ARGUMENT_CONTRACT").GetString());
        Assert.Equal(["--value", "3"], launch.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "exited" }));
        await runTask;
    }
}
