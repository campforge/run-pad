using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Invoklet.Build.Core.ProcessRunners;
using Invoklet.Contracts.Debugging;
using Invoklet.Contracts.Models;
using Invoklet.Debug;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests.Debug;

// Exception stops as netcoredbg 3.2 reports them for a script whose first statement is
// int.Parse("not-a-number"): "stopped" with reason "exception" and a "text" line, the details
// behind "exceptionInfo", and a failed response (0x80004005) — with no further event — to a step
// requested from that stop. The stop must keep its reason and exception, and a rejected step must
// present the same stop again instead of leaving the session waiting for a "stopped" event that
// never comes. Same fake-netcoredbg-conversation technique as DapSessionSourcePathTests.cs.
public class DapSessionExceptionStopTests
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

    private sealed class TestBreakpoint(int line) : IBreakpoint
    {
        public string FileName => "";
        public int Line { get; set; } = line;
        public string? Condition => null;
        public string? HitCondition => null;
    }

    private const string UnhandledText = "An unhandled exception of type 'System.FormatException' occurred in System.Private.CoreLib.dll";

    private static byte[] Frame(object body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
        return [.. Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n"), .. bodyBytes];
    }

    private static List<string> WrittenCommands(MemoryStream input)
    {
        var result = new List<string>();
        foreach (var part in Encoding.UTF8.GetString(input.ToArray()).Split("Content-Length: ", StringSplitOptions.RemoveEmptyEntries))
        {
            var bodyStart = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (bodyStart < 0) continue;
            using var doc = JsonDocument.Parse(part[(bodyStart + 4)..]);
            result.Add(doc.RootElement.GetProperty("command").GetString()!);
        }

        return result;
    }

    // seq of the count-th written request with this command.
    private static int RequestSeq(MemoryStream input, string command, int count = 1)
    {
        var seqs = new List<int>();
        foreach (var part in Encoding.UTF8.GetString(input.ToArray()).Split("Content-Length: ", StringSplitOptions.RemoveEmptyEntries))
        {
            var bodyStart = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (bodyStart < 0) continue;
            using var doc = JsonDocument.Parse(part[(bodyStart + 4)..]);
            if (doc.RootElement.GetProperty("command").GetString() == command)
                seqs.Add(doc.RootElement.GetProperty("seq").GetInt32());
        }

        return seqs[count - 1];
    }

    private static async Task WaitForRequestAsync(MemoryStream input, string command, int count = 1)
    {
        for (var i = 0; i < 100; i++)
        {
            if (WrittenCommands(input).Count(c => c == command) >= count) return;
            await Task.Delay(20);
        }

        throw new TimeoutException($"""Never saw written "{command}" request #{count}.""");
    }

    private static object Response(int seq, string command, object? body = null, bool success = true, string? message = null, int? requestSeq = null) =>
        new { seq, type = "response", request_seq = requestSeq ?? seq, command, success, message, body = body ?? new { } };

    private static object ExceptionInfoBody(string typeName) => new
    {
        exceptionId = typeName,
        breakMode = "unhandled",
        details = new { message = "details of " + typeName, fullTypeName = typeName },
    };

    private static readonly TimeSpan ShortExceptionInfoTimeout = TimeSpan.FromMilliseconds(300);

    private static async Task HandshakeAsync(FakeChildProcess process, bool supportsExceptionInfo)
    {
        process.Output.Push(Frame(Response(1, "initialize", new
        {
            supportsExceptionInfoRequest = supportsExceptionInfo,
            exceptionBreakpointFilters = new[] { new { filter = "user-unhandled", label = "user-unhandled" }, new { filter = "all", label = "all" } },
        })));
        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "initialized" }));
        await WaitForRequestAsync(process.Input, "setBreakpoints");
        process.Output.Push(Frame(Response(3, "setBreakpoints", new { breakpoints = new[] { new { verified = true, line = 1 } } })));
        await WaitForRequestAsync(process.Input, "setExceptionBreakpoints");
        process.Output.Push(Frame(Response(4, "setExceptionBreakpoints")));
        await WaitForRequestAsync(process.Input, "configurationDone");
    }

    // Answers the threads/stackTrace/scopes/variables round trips of one stop on user line 1; the
    // count-th such stop when the same position is presented again.
    private static async Task AnswerStopQueriesAsync(FakeChildProcess process, int count = 1)
    {
        await WaitForRequestAsync(process.Input, "threads", count);
        process.Output.Push(Frame(Response(11, "threads", new { threads = new[] { new { id = 1, name = "Main" } } })));
        await WaitForRequestAsync(process.Input, "stackTrace", count);
        process.Output.Push(Frame(Response(12, "stackTrace", new
        {
            stackFrames = new[] { new { id = 100, name = "Program.<Main>$(String[] args)", line = 1, column = 1, source = new { path = "/out/InvokletQuery.cs" } } },
        })));
        await WaitForRequestAsync(process.Input, "scopes", count);
        process.Output.Push(Frame(Response(13, "scopes", new { scopes = new[] { new { name = "Locals", variablesReference = 1, expensive = false } } })));
        await WaitForRequestAsync(process.Input, "variables", count);
        process.Output.Push(Frame(Response(14, "variables", new
        {
            variables = new[] { new { name = "$exception", value = "{System.FormatException}", type = "System.FormatException", variablesReference = 2 } },
        })));
    }

    private static DapSession CreateSession(
        FakeChildProcess process, Action<IBreakpointHitContext> onHit, TimeSpan? exceptionInfoTimeout = null) => new(
        process,
        new DapSessionOptions(
            ProgramPath: "/out/bin/InvokletQuery.dll",
            WorkingDirectory: "/out",
            Breakpoints: [new TestBreakpoint(1)],
            SourceFileExtension: ".cs",
            LineOffset: 0,
            ExceptionInfoTimeout: exceptionInfoTimeout),
        outputHandler: null,
        onBreakpointHit: onHit,
        logger: NullLogger<DapSession>.Instance);

    private static object ExceptionStopped() =>
        new { seq = 10, type = "event", @event = "stopped", body = new { reason = "exception", text = UnhandledText, threadId = 1, allThreadsStopped = true } };

    [Fact]
    public async Task ExceptionStop_Should_CarryTheReasonAndTheExceptionInfoDetails()
    {
        var hit = new TaskCompletionSource<IBreakpointHitContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeChildProcess();
        await using var session = CreateSession(process, ctx => hit.TrySetResult(ctx));
        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        await HandshakeAsync(process, supportsExceptionInfo: true);

        process.Output.Push(Frame(ExceptionStopped()));
        await AnswerStopQueriesAsync(process);
        await WaitForRequestAsync(process.Input, "exceptionInfo");
        process.Output.Push(Frame(Response(15, "exceptionInfo", requestSeq: RequestSeq(process.Input, "exceptionInfo"), body: new
        {
            exceptionId = "System.FormatException",
            description = "The input string 'not-a-number' was not in a correct format.",
            breakMode = "unhandled",
            details = new
            {
                message = "The input string 'not-a-number' was not in a correct format.",
                typeName = "FormatException",
                fullTypeName = "System.FormatException",
                stackTrace = "   at System.Int32.Parse(String s)\n   at Program.<Main>$(String[] args) in /out/InvokletQuery.cs:line 1",
                innerException = new[] { new { message = "inner", fullTypeName = "System.InvalidOperationException" } },
            },
        })));

        var context = await hit.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(DebugStopReason.Exception, context.StopReason);
        Assert.Null(context.ResumeError);
        var exception = Assert.IsType<DebugExceptionInfo>(context.Exception);
        Assert.Equal("System.FormatException", exception.TypeName);
        Assert.Equal("The input string 'not-a-number' was not in a correct format.", exception.Message);
        Assert.Equal(UnhandledText, exception.Description);
        Assert.Contains("Int32.Parse", exception.StackTrace);
        Assert.True(exception.IsUnhandled);
        Assert.Equal("System.InvalidOperationException", exception.InnerException?.TypeName);
        // Thrown on the breakpoint's line, but not a hit of that breakpoint.
        Assert.IsType<SyntheticBreakpoint>(context.Breakpoint);
        Assert.Equal(1, context.CallStack[0].Line);
        Assert.Equal(["threads", "stackTrace", "scopes", "variables", "exceptionInfo"],
            WrittenCommands(process.Input).SkipWhile(c => c != "threads").ToList());

        context.Resume(DebugAction.Continue);
        await WaitForRequestAsync(process.Input, "continue");
        process.Output.Push(Frame(new { seq = 20, type = "event", @event = "exited" }));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true)]  // supported, but the request fails
    [InlineData(false)] // not declared: never asked
    public async Task ExceptionStop_Should_FallBackToTheStopText_WithoutExceptionInfo(bool supportsExceptionInfo)
    {
        var hit = new TaskCompletionSource<IBreakpointHitContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeChildProcess();
        await using var session = CreateSession(process, ctx => hit.TrySetResult(ctx));
        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        await HandshakeAsync(process, supportsExceptionInfo);

        process.Output.Push(Frame(ExceptionStopped()));
        await AnswerStopQueriesAsync(process);
        if (supportsExceptionInfo)
        {
            await WaitForRequestAsync(process.Input, "exceptionInfo");
            process.Output.Push(Frame(Response(15, "exceptionInfo", success: false, message: "Failed command 'exceptionInfo' : 0x80004005",
                requestSeq: RequestSeq(process.Input, "exceptionInfo"))));
        }

        var context = await hit.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(DebugStopReason.Exception, context.StopReason);
        var exception = Assert.IsType<DebugExceptionInfo>(context.Exception);
        Assert.Equal("System.FormatException", exception.TypeName); // the "$exception" local's type
        Assert.Null(exception.Message);
        Assert.Equal(UnhandledText, exception.Description);
        Assert.False(exception.IsUnhandled);
        if (!supportsExceptionInfo)
            Assert.DoesNotContain("exceptionInfo", WrittenCommands(process.Input));

        context.Resume(DebugAction.Continue);
        await WaitForRequestAsync(process.Input, "continue");
        process.Output.Push(Frame(new { seq = 20, type = "event", @event = "exited" }));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RejectedStep_Should_PresentTheSameStopAgain_WithTheDebuggersError()
    {
        var hits = Channel.CreateUnbounded<IBreakpointHitContext>();
        var process = new FakeChildProcess();
        await using var session = CreateSession(process, ctx => hits.Writer.TryWrite(ctx));
        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        await HandshakeAsync(process, supportsExceptionInfo: false);

        process.Output.Push(Frame(ExceptionStopped()));
        await AnswerStopQueriesAsync(process);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var first = await hits.Reader.ReadAsync(timeout.Token);

        first.Resume(DebugAction.StepOver);
        await WaitForRequestAsync(process.Input, "next");
        process.Output.Push(Frame(Response(16, "next", success: false, message: "Failed command 'next' : 0x80004005")));
        await AnswerStopQueriesAsync(process, count: 2);
        var again = await hits.Reader.ReadAsync(timeout.Token);

        Assert.Equal(DebugStopReason.Exception, again.StopReason);
        Assert.Equal("The debugger couldn't step over: Failed command 'next' : 0x80004005", again.ResumeError);
        Assert.Equal(UnhandledText, again.Exception?.Description);
        Assert.Equal(1, again.CallStack[0].Line);

        again.Resume(DebugAction.Continue);
        await WaitForRequestAsync(process.Input, "continue");
        process.Output.Push(Frame(new { seq = 20, type = "event", @event = "exited" }));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BreakpointStop_Should_KeepItsReason_WithoutAnException()
    {
        var hit = new TaskCompletionSource<IBreakpointHitContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeChildProcess();
        await using var session = CreateSession(process, ctx => hit.TrySetResult(ctx));
        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        await HandshakeAsync(process, supportsExceptionInfo: true);

        process.Output.Push(Frame(new { seq = 10, type = "event", @event = "stopped", body = new { reason = "breakpoint", threadId = 1 } }));
        await AnswerStopQueriesAsync(process);
        var context = await hit.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(DebugStopReason.Breakpoint, context.StopReason);
        Assert.Null(context.Exception);
        Assert.IsType<TestBreakpoint>(context.Breakpoint);
        Assert.DoesNotContain("exceptionInfo", WrittenCommands(process.Input));

        context.Resume(DebugAction.Continue);
        await WaitForRequestAsync(process.Input, "continue");
        process.Output.Push(Frame(new { seq = 20, type = "event", @event = "exited" }));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // An adapter that declares exceptionInfo and never answers it: the stop is still shown, with the
    // stop's own text, once the bounded wait ends — and the session keeps working. The answer that
    // finally arrives must be skipped cleanly (framing intact), and must not be taken for the answer
    // to the next stop's exceptionInfo.
    [Fact]
    public async Task UnansweredExceptionInfo_Should_ShowTheStopAfterTheTimeout_AndSkipTheLateAnswer()
    {
        var hits = Channel.CreateUnbounded<IBreakpointHitContext>();
        var process = new FakeChildProcess();
        await using var session = CreateSession(process, ctx => hits.Writer.TryWrite(ctx), ShortExceptionInfoTimeout);
        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        await HandshakeAsync(process, supportsExceptionInfo: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        process.Output.Push(Frame(ExceptionStopped()));
        await AnswerStopQueriesAsync(process);
        await WaitForRequestAsync(process.Input, "exceptionInfo");
        var first = await hits.Reader.ReadAsync(timeout.Token); // no answer pushed

        Assert.Equal(DebugStopReason.Exception, first.StopReason);
        Assert.Equal(UnhandledText, first.Exception?.Description);
        Assert.Equal("System.FormatException", first.Exception?.TypeName); // "$exception" local
        Assert.Null(first.Exception?.Message);

        // The late answer arrives while paused, then the user continues to a second exception stop.
        var firstSeq = RequestSeq(process.Input, "exceptionInfo");
        process.Output.Push(Frame(Response(30, "exceptionInfo", ExceptionInfoBody("Late.FirstException"), requestSeq: firstSeq)));
        first.Resume(DebugAction.Continue);
        await WaitForRequestAsync(process.Input, "continue");
        process.Output.Push(Frame(ExceptionStopped()));
        await AnswerStopQueriesAsync(process, count: 2);
        await WaitForRequestAsync(process.Input, "exceptionInfo", count: 2);
        process.Output.Push(Frame(Response(31, "exceptionInfo", ExceptionInfoBody("System.FormatException"),
            requestSeq: RequestSeq(process.Input, "exceptionInfo", count: 2))));
        var second = await hits.Reader.ReadAsync(timeout.Token);

        Assert.Equal("System.FormatException", second.Exception?.TypeName);
        Assert.Equal("details of System.FormatException", second.Exception?.Message);
        Assert.True(second.Exception?.IsUnhandled);

        second.Resume(DebugAction.Continue);
        await WaitForRequestAsync(process.Input, "continue", count: 2);
        process.Output.Push(Frame(new { seq = 40, type = "event", @event = "exited" }));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // The answer to a stop's exceptionInfo was already late when its wait ended; a second, later
    // reply to an older request shows up mid-way through the next stop's queries — both skipped.
    [Fact]
    public async Task LateExceptionInfoAnswer_Should_NotBeMistakenForTheNextStopsAnswer()
    {
        var hits = Channel.CreateUnbounded<IBreakpointHitContext>();
        var process = new FakeChildProcess();
        await using var session = CreateSession(process, ctx => hits.Writer.TryWrite(ctx), ShortExceptionInfoTimeout);
        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        await HandshakeAsync(process, supportsExceptionInfo: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        process.Output.Push(Frame(ExceptionStopped()));
        await AnswerStopQueriesAsync(process);
        await WaitForRequestAsync(process.Input, "exceptionInfo");
        var first = await hits.Reader.ReadAsync(timeout.Token);
        first.Resume(DebugAction.Continue);
        await WaitForRequestAsync(process.Input, "continue");

        process.Output.Push(Frame(ExceptionStopped()));
        await AnswerStopQueriesAsync(process, count: 2);
        await WaitForRequestAsync(process.Input, "exceptionInfo", count: 2);
        // The first request's answer comes only now, right before the second's.
        process.Output.Push(Frame(Response(30, "exceptionInfo", ExceptionInfoBody("Late.FirstException"),
            requestSeq: RequestSeq(process.Input, "exceptionInfo"))));
        process.Output.Push(Frame(Response(31, "exceptionInfo", ExceptionInfoBody("System.FormatException"),
            requestSeq: RequestSeq(process.Input, "exceptionInfo", count: 2))));
        var second = await hits.Reader.ReadAsync(timeout.Token);

        Assert.Equal("System.FormatException", second.Exception?.TypeName);
        second.Resume(DebugAction.Continue);
        await WaitForRequestAsync(process.Input, "continue", count: 2);
        process.Output.Push(Frame(new { seq = 40, type = "event", @event = "exited" }));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // Stop pressed while paused on a stop whose exceptionInfo never came: the session still ends
    // promptly through cancellation and kills the debugger, with the abandoned wait not in the way.
    [Fact]
    public async Task UnansweredExceptionInfo_Should_StillLetStopEndTheSession()
    {
        var hit = new TaskCompletionSource<IBreakpointHitContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeChildProcess();
        await using var session = CreateSession(process, ctx => hit.TrySetResult(ctx), ShortExceptionInfoTimeout);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runTask = session.RunAsync(runCts.Token);
        await HandshakeAsync(process, supportsExceptionInfo: true);

        process.Output.Push(Frame(ExceptionStopped()));
        await AnswerStopQueriesAsync(process);
        var context = await hit.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(DebugStopReason.Exception, context.StopReason);

        await runCts.CancelAsync(); // what Stop does
        var ended = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(runTask, ended);
        if (runTask.IsFaulted)
            Assert.IsAssignableFrom<OperationCanceledException>(runTask.Exception!.InnerException);
        Assert.True(process.Killed);
    }
}
