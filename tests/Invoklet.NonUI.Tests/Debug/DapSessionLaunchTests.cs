using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Invoklet.Build.Core.ProcessRunners;
using Invoklet.Contracts.Models;
using Invoklet.Debug;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests.Debug;

// Live report: Run works on macOS, Debug doesn't. The session
// log showed netcoredbg rejecting setExceptionBreakpoints(["unhandled"]) with 0x80070057 (it only
// declares "user-unhandled"/"all") and then configurationDone failing with 0x80070002 — netcoredbg
// turns a bare ".dll" program into a PATH lookup of "dotnet", which a macOS GUI app's launchd PATH
// doesn't contain — after which the session silently waited forever. Same fake-netcoredbg-conversation
// technique as DapSessionCurrentQueryPathTests.cs (PipeStream/Frame duplicated, files stay self-contained).
[Trait("Category", "Integration")]
[Trait("Category", "Debugger")]
public class DapSessionLaunchTests
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
        public bool Killed { get; private set; }

        public StreamReader StandardOutput { get; } = new(new MemoryStream());
        public StreamReader StandardError { get; } = new(new MemoryStream());
        public Stream OutputStream => Output;
        public Stream InputStream => Input;

        public bool Start() => true;
        public void Kill() => Killed = true;
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

    // Splits everything the session wrote so far into its individual DAP request bodies.
    private static List<JsonElement> WrittenRequests(MemoryStream input)
    {
        var text = Encoding.UTF8.GetString(input.ToArray());
        var result = new List<JsonElement>();
        foreach (var part in text.Split("Content-Length: ", StringSplitOptions.RemoveEmptyEntries))
        {
            var bodyStart = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (bodyStart < 0) continue;
            using var doc = JsonDocument.Parse(part[(bodyStart + 4)..]);
            result.Add(doc.RootElement.Clone());
        }

        return result;
    }

    private static async Task<JsonElement> WaitForRequestAsync(MemoryStream input, string command)
    {
        for (var i = 0; i < 100; i++)
        {
            var match = WrittenRequests(input).FirstOrDefault(r => r.GetProperty("command").GetString() == command);
            if (match.ValueKind != JsonValueKind.Undefined) return match;
            await Task.Delay(20);
        }

        throw new TimeoutException($"""Never saw a written "{command}" request.""");
    }

    private static object InitializeResponse(params string[] filters) => new
    {
        seq = 1,
        type = "response",
        request_seq = 1,
        command = "initialize",
        success = true,
        body = new { exceptionBreakpointFilters = filters.Select(f => new { filter = f, label = f }).ToArray() },
    };

    private static DapSession CreateSession(
        FakeChildProcess process,
        string? runtimePath = null,
        bool breakOnAll = false,
        bool breakOnUncaught = true,
        List<(string Data, OutputKind Kind)>? output = null,
        IReadOnlyList<string>? arguments = null)
    {
        var options = new DapSessionOptions(
            ProgramPath: "/tmp/q/bin/InvokletQuery.dll",
            WorkingDirectory: "/tmp/q",
            Breakpoints: [],
            SourceFileExtension: ".cs",
            LineOffset: 0,
            BreakOnAllExceptions: breakOnAll,
            BreakOnUncaughtExceptions: breakOnUncaught,
            RuntimePath: runtimePath,
            Arguments: arguments);

        return new DapSession(
            process, options,
            outputHandler: output is null ? null : (data, kind) => { lock (output) output.Add((data, kind)); },
            onBreakpointHit: static _ => { },
            logger: NullLogger<DapSession>.Instance);
    }

    [Fact]
    public async Task RunAsync_Should_LaunchThroughTheConfiguredRuntime_WithTheAssemblyAsArgument()
    {
        var process = new FakeChildProcess();
        await using var session = CreateSession(process, runtimePath: "/usr/local/share/dotnet/dotnet");

        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        process.Output.Push(Frame(InitializeResponse("user-unhandled", "all")));
        var launch = await WaitForRequestAsync(process.Input, "launch");

        var args = launch.GetProperty("arguments");
        Assert.Equal("/usr/local/share/dotnet/dotnet", args.GetProperty("program").GetString());
        Assert.Equal(["/tmp/q/bin/InvokletQuery.dll"], args.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("/tmp/q", args.GetProperty("cwd").GetString());

        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "exited" }));
        await runTask;
    }

    [Fact]
    public async Task RunAsync_Should_KeepLaunchingTheAssemblyDirectly_WhenNoRuntimePathIsGiven()
    {
        var process = new FakeChildProcess();
        await using var session = CreateSession(process, runtimePath: null);

        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        process.Output.Push(Frame(InitializeResponse("user-unhandled", "all")));
        var launch = await WaitForRequestAsync(process.Input, "launch");

        var args = launch.GetProperty("arguments");
        Assert.Equal("/tmp/q/bin/InvokletQuery.dll", args.GetProperty("program").GetString());
        Assert.False(args.TryGetProperty("args", out _));

        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "exited" }));
        await runTask;
    }

    [Theory]
    [InlineData("/usr/local/share/dotnet/dotnet", new[] { "/tmp/q/bin/InvokletQuery.dll", "a b", "--x" })]
    [InlineData(null, new[] { "a b", "--x" })]
    public async Task RunAsync_Should_PassTheScriptArguments_AfterTheAssembly(string? runtimePath, string[] expected)
    {
        var process = new FakeChildProcess();
        await using var session = CreateSession(process, runtimePath: runtimePath, arguments: ["a b", "--x"]);

        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        process.Output.Push(Frame(InitializeResponse("user-unhandled", "all")));
        var launch = await WaitForRequestAsync(process.Input, "launch");

        var args = launch.GetProperty("arguments");
        Assert.Equal(expected, args.GetProperty("args").EnumerateArray().Select(a => a.GetString()));

        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "exited" }));
        await runTask;
    }

    // Uncaught → "unhandled" when declared (standard id), else netcoredbg's "user-unhandled";
    // break-on-all → "all". Never an undeclared id.
    [Theory]
    [InlineData(new[] { "user-unhandled", "all" }, false, true, new[] { "user-unhandled" })]
    [InlineData(new[] { "unhandled", "user-unhandled", "all" }, false, true, new[] { "unhandled" })]
    [InlineData(new[] { "user-unhandled", "all" }, true, true, new[] { "all" })]
    [InlineData(new[] { "user-unhandled" }, true, false, new[] { "user-unhandled" })]
    [InlineData(new[] { "user-unhandled", "all" }, false, false, new string[0])]
    [InlineData(new string[0], false, true, new string[0])]
    public async Task RunAsync_Should_SendOnlyDeclaredExceptionFilters(
        string[] declared, bool breakOnAll, bool breakOnUncaught, string[] expected)
    {
        var process = new FakeChildProcess();
        await using var session = CreateSession(process, breakOnAll: breakOnAll, breakOnUncaught: breakOnUncaught);

        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        process.Output.Push(Frame(InitializeResponse(declared)));
        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "initialized" }));
        var request = await WaitForRequestAsync(process.Input, "setExceptionBreakpoints");

        Assert.Equal(expected, request.GetProperty("arguments").GetProperty("filters").EnumerateArray().Select(f => f.GetString()));

        process.Output.Push(Frame(new { seq = 3, type = "event", @event = "exited" }));
        await runTask;
    }

    // An "exited" event consumed while waiting for some other response used to be forgotten, leaving
    // the main loop waiting forever for it (netcoredbg itself keeps running until "disconnect").
    [Fact]
    public async Task RunAsync_Should_Finish_WhenExitedArrivesWhileWaitingForAResponse()
    {
        var process = new FakeChildProcess();
        await using var session = CreateSession(process);

        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        process.Output.Push(Frame(InitializeResponse("user-unhandled", "all")));
        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "initialized" }));
        await WaitForRequestAsync(process.Input, "setExceptionBreakpoints");
        process.Output.Push(Frame(new { seq = 3, type = "event", @event = "exited" }));

        await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains(WrittenRequests(process.Input), r => r.GetProperty("command").GetString() == "disconnect");
    }

    [Fact]
    public async Task RunAsync_Should_WarnAndContinue_WhenNoUncaughtFilterIsDeclared()
    {
        var process = new FakeChildProcess();
        var output = new List<(string Data, OutputKind Kind)>();
        await using var session = CreateSession(process, output: output);

        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        process.Output.Push(Frame(InitializeResponse("all")));
        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "initialized" }));
        await WaitForRequestAsync(process.Input, "setExceptionBreakpoints");
        process.Output.Push(Frame(new { seq = 3, type = "response", request_seq = 3, command = "setExceptionBreakpoints", success = true }));
        await WaitForRequestAsync(process.Input, "configurationDone");

        lock (output)
            Assert.Contains(output, o => o.Kind == OutputKind.StandardError && o.Data.Contains("uncaught exceptions"));

        process.Output.Push(Frame(new { seq = 4, type = "event", @event = "exited" }));
        await runTask;
    }

    [Fact]
    public async Task RunAsync_Should_ReportRejectedExceptionFilters_AndStillSendConfigurationDone()
    {
        var process = new FakeChildProcess();
        var output = new List<(string Data, OutputKind Kind)>();
        await using var session = CreateSession(process, output: output);

        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        process.Output.Push(Frame(InitializeResponse("user-unhandled", "all")));
        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "initialized" }));
        await WaitForRequestAsync(process.Input, "setExceptionBreakpoints");
        process.Output.Push(Frame(new
        {
            seq = 3,
            type = "response",
            request_seq = 3,
            command = "setExceptionBreakpoints",
            success = false,
            message = "Failed command 'setExceptionBreakpoints' : 0x80070057",
        }));
        await WaitForRequestAsync(process.Input, "configurationDone");

        lock (output)
            Assert.Contains(output, o => o.Kind == OutputKind.StandardError && o.Data.Contains("0x80070057"));

        process.Output.Push(Frame(new { seq = 4, type = "event", @event = "exited" }));
        await runTask;
    }

    // The exact failing sequence from the macOS log: launch is acknowledged, then configurationDone
    // fails — previously the session ignored it and waited forever.
    [Fact]
    public async Task RunAsync_Should_Throw_WhenConfigurationDoneFails()
    {
        var process = new FakeChildProcess();
        await using var session = CreateSession(process, runtimePath: "/usr/local/share/dotnet/dotnet");

        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        process.Output.Push(Frame(InitializeResponse("user-unhandled", "all")));
        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "initialized" }));
        await WaitForRequestAsync(process.Input, "setExceptionBreakpoints");
        process.Output.Push(Frame(new { seq = 3, type = "response", request_seq = 2, command = "launch", success = true }));
        process.Output.Push(Frame(new { seq = 4, type = "response", request_seq = 3, command = "setExceptionBreakpoints", success = true }));
        await WaitForRequestAsync(process.Input, "configurationDone");
        process.Output.Push(Frame(new
        {
            seq = 5,
            type = "response",
            request_seq = 4,
            command = "configurationDone",
            success = false,
            message = "Failed command 'configurationDone' : 0x80070002",
        }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runTask)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains("configurationDone", ex.Message);
        Assert.Contains("0x80070002", ex.Message);
        Assert.Contains("/usr/local/share/dotnet/dotnet", ex.Message);
        Assert.True(process.Killed);
    }

    // netcoredbg answers "launch" after "initialized", i.e. while the session is blocked reading the
    // setExceptionBreakpoints response — a failure there must not be silently dropped.
    [Fact]
    public async Task RunAsync_Should_Throw_WhenLaunchFails_WhileWaitingForAnotherResponse()
    {
        var process = new FakeChildProcess();
        await using var session = CreateSession(process);

        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        process.Output.Push(Frame(InitializeResponse("user-unhandled", "all")));
        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "initialized" }));
        await WaitForRequestAsync(process.Input, "setExceptionBreakpoints");
        process.Output.Push(Frame(new
        {
            seq = 3,
            type = "response",
            request_seq = 2,
            command = "launch",
            success = false,
            message = "Failed command 'launch' : 0x80070002",
        }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runTask)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains("'launch'", ex.Message);
        Assert.Contains("0x80070002", ex.Message);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task RunAsync_Should_Throw_WhenInitializeFails()
    {
        var process = new FakeChildProcess();
        await using var session = CreateSession(process);

        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        process.Output.Push(Frame(new
        {
            seq = 1,
            type = "response",
            request_seq = 1,
            command = "initialize",
            success = false,
            message = "boom",
        }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runTask)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains("boom", ex.Message);
        Assert.DoesNotContain(WrittenRequests(process.Input), r => r.GetProperty("command").GetString() == "launch");
    }
}
