using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Invoklet.Build.Core.ProcessRunners;
using Invoklet.Contracts;
using Invoklet.Contracts.Models;
using Invoklet.Debug;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Live regression: every breakpoint stayed pending on macOS
// because setBreakpoints named /var/folders/.../InvokletQuery.cs while the PDB (built with the
// symlink-resolved working directory) recorded /private/var/folders/... . The breakpoints here live
// under a symlinked ANCESTOR directory; the session must send the canonical path for the initial
// set, gutter edits made during the session and run-to-cursor, and still map a stop reported at the
// canonical path (with the kind-transform line offset) back to the user's own breakpoint.
// Same fake-netcoredbg-conversation technique as DapSessionLaunchTests.cs (helpers duplicated).
public class DapSessionSourcePathTests : IDisposable
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

    private sealed class TestBreakpoint(string fileName, int line) : IBreakpoint
    {
        public string FileName { get; } = fileName;
        public int Line { get; set; } = line;
        public string? Condition => null;
        public string? HitCondition => null;
    }

    private const int LineOffset = 1;

    private readonly string _root;
    private readonly string _aliasDir;
    private readonly string _canonicalSource;

    public DapSessionSourcePathTests()
    {
        _root = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", "Invoklet-dap-srcpath-" + Guid.NewGuid());
        var realDir = Directory.CreateDirectory(Path.Combine(_root, "real", "out")).FullName;
        File.WriteAllText(Path.Combine(realDir, "InvokletQuery.cs"), "int x = 1;");
        _aliasDir = Path.Combine(_root, "alias", "out");
        if (!OperatingSystem.IsWindows())
            Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), Path.Combine(_root, "real"));
        _canonicalSource = DebugSourcePath.Canonicalize(Path.Combine(realDir, "InvokletQuery.cs"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] Frame(object body)
    {
        var json = JsonSerializer.Serialize(body);
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
        return [.. header, .. bodyBytes];
    }

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

    // Waits for the count-th (1-based) written request with this command.
    private static async Task<JsonElement> WaitForRequestAsync(MemoryStream input, string command, int count = 1)
    {
        for (var i = 0; i < 100; i++)
        {
            var matches = WrittenRequests(input).Where(r => r.GetProperty("command").GetString() == command).ToList();
            if (matches.Count >= count) return matches[count - 1];
            await Task.Delay(20);
        }

        throw new TimeoutException($"""Never saw written "{command}" request #{count}.""");
    }

    private static (string? Path, List<int> Lines) SetBreakpointsArgs(JsonElement request)
    {
        var args = request.GetProperty("arguments");
        return (
            args.GetProperty("source").GetProperty("path").GetString(),
            args.GetProperty("breakpoints").EnumerateArray().Select(b => b.GetProperty("line").GetInt32()).ToList());
    }

    private static object Response(int seq, string command, object? body = null) =>
        new { seq, type = "response", request_seq = seq, command, success = true, body = body ?? new { } };

    // Drives the handshake up to configurationDone and returns once the session is "running".
    private static async Task HandshakeAsync(FakeChildProcess process)
    {
        process.Output.Push(Frame(Response(1, "initialize", new { exceptionBreakpointFilters = new[] { new { filter = "all", label = "all" } } })));
        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "initialized" }));
        await WaitForRequestAsync(process.Input, "setBreakpoints");
        process.Output.Push(Frame(Response(3, "setBreakpoints", new { breakpoints = new[] { new { verified = false, line = 2 + LineOffset } } })));
        await WaitForRequestAsync(process.Input, "setExceptionBreakpoints");
        process.Output.Push(Frame(Response(4, "setExceptionBreakpoints")));
        await WaitForRequestAsync(process.Input, "configurationDone");
    }

    // Answers the threads/stackTrace/scopes round trips HandleStoppedAsync makes for a stop at the
    // canonical source path, compiled line = user line + LineOffset.
    private async Task StopAtAsync(FakeChildProcess process, int userLine)
    {
        process.Output.Push(Frame(new { seq = 10, type = "event", @event = "stopped", body = new { reason = "breakpoint", threadId = 1 } }));
        await WaitForRequestAsync(process.Input, "threads");
        process.Output.Push(Frame(Response(11, "threads", new { threads = new[] { new { id = 1, name = "Main" } } })));
        await WaitForRequestAsync(process.Input, "stackTrace");
        process.Output.Push(Frame(Response(12, "stackTrace", new
        {
            stackFrames = new[] { new { id = 100, name = "<Main>$", line = userLine + LineOffset, column = 1, source = new { path = _canonicalSource } } },
        })));
        await WaitForRequestAsync(process.Input, "scopes");
        process.Output.Push(Frame(Response(13, "scopes", new { scopes = Array.Empty<object>() })));
    }

    // The while-running resend (SendBreakpointsWhileRunningAsync) reuses the same _sourceFile as the
    // paused resend checked here; its timing is covered by DapSessionRunningBreakpointResyncTests.
    [Fact]
    public async Task Session_Should_UseTheCanonicalSourcePath_ForInitialRuntimeAddedAndRunToCursorBreakpoints()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Paths are left unchanged on Windows.");
        var userBreakpoint = new TestBreakpoint(Path.Combine(_aliasDir, "InvokletQuery.cs"), 2);
        var breakpoints = new List<IBreakpoint> { userBreakpoint };
        var verified = new List<(IBreakpoint Bp, bool Verified)>();
        var hit = new TaskCompletionSource<IBreakpointHitContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeChildProcess();
        await using var session = new DapSession(
            process,
            new DapSessionOptions(
                ProgramPath: Path.Combine(_aliasDir, "bin", "InvokletQuery.dll"),
                WorkingDirectory: _aliasDir,
                Breakpoints: breakpoints,
                SourceFileExtension: ".cs",
                LineOffset: LineOffset),
            outputHandler: null,
            onBreakpointHit: ctx => hit.TrySetResult(ctx),
            logger: NullLogger<DapSession>.Instance,
            onBreakpointVerified: (bp, v, _) => { lock (verified) verified.Add((bp, v)); });

        var runTask = session.RunAsync(TestContext.Current.CancellationToken);
        await HandshakeAsync(process);

        var (initialPath, initialLines) = SetBreakpointsArgs(await WaitForRequestAsync(process.Input, "setBreakpoints"));
        Assert.Equal(_canonicalSource, initialPath);
        Assert.Equal([2 + LineOffset], initialLines);

        // netcoredbg resolves the pending breakpoint once the module loads, reporting the PDB's path.
        process.Output.Push(Frame(new
        {
            seq = 5,
            type = "event",
            @event = "breakpoint",
            body = new { reason = "changed", breakpoint = new { verified = true, line = 2 + LineOffset, source = new { path = _canonicalSource } } },
        }));

        await StopAtAsync(process, userLine: 2);
        var context = await hit.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Same(userBreakpoint, context.Breakpoint);
        Assert.Equal(2, context.CallStack[0].Line);
        lock (verified)
            Assert.Contains(verified, v => ReferenceEquals(v.Bp, userBreakpoint) && v.Verified);

        // A gutter breakpoint added while paused (resent before resuming), then run-to-cursor.
        breakpoints.Add(new TestBreakpoint(Path.Combine(_aliasDir, "InvokletQuery.cs"), 5));
        session.RequestBreakpointResync();
        await context.RunToCursorAsync(7, TestContext.Current.CancellationToken);

        var (resendPath, resendLines) = SetBreakpointsArgs(await WaitForRequestAsync(process.Input, "setBreakpoints", count: 2));
        Assert.Equal(_canonicalSource, resendPath);
        Assert.Equal([2 + LineOffset, 5 + LineOffset, 7 + LineOffset], resendLines);
        process.Output.Push(Frame(Response(14, "setBreakpoints", new { breakpoints = Array.Empty<object>() })));
        await WaitForRequestAsync(process.Input, "continue");

        process.Output.Push(Frame(new { seq = 20, type = "event", @event = "exited" }));
        await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // Stop while paused at a breakpoint: the pending resume wait must observe cancellation, end the
    // session and kill netcoredbg instead of leaving the debuggee suspended.
    [Fact]
    public async Task RunAsync_Should_EndAndKillTheDebugger_WhenCancelledWhilePaused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses the symlinked fixture directory.");
        var hit = new TaskCompletionSource<IBreakpointHitContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeChildProcess();
        await using var session = new DapSession(
            process,
            new DapSessionOptions(
                ProgramPath: Path.Combine(_aliasDir, "bin", "InvokletQuery.dll"),
                WorkingDirectory: _aliasDir,
                Breakpoints: [new TestBreakpoint(Path.Combine(_aliasDir, "InvokletQuery.cs"), 2)],
                SourceFileExtension: ".cs",
                LineOffset: LineOffset),
            outputHandler: null,
            onBreakpointHit: ctx => hit.TrySetResult(ctx),
            logger: NullLogger<DapSession>.Instance);

        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runTask = session.RunAsync(stopCts.Token);
        await HandshakeAsync(process);
        await StopAtAsync(process, userLine: 2);
        await hit.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await stopCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(process.Killed);
    }
}
