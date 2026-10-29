using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Invoklet.Build.Core.ProcessRunners;
using Invoklet.Contracts;
using Invoklet.Debug;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Live user report: "dynamic breakpoint set/unset doesn't work on a running
// script" — see DapSession.RequestBreakpointResync's own comment for the root cause: the resync
// used to only fire the next time HandleStoppedAsync reached its pre-resume check, which may never
// happen at all if nothing else was ever going to stop execution again. These tests script a full
// fake netcoredbg conversation to reach the exact window the bug lived in: past the initial
// handshake, with the main protocol loop genuinely blocked waiting for the next message (i.e.
// actually running, not paused) — DapSessionTests.cs's existing FakeChildProcess uses a plain
// MemoryStream for OutputStream, which returns EOF once its buffer is exhausted rather than
// blocking for more, so it can't represent "still running, nothing more has arrived yet" the way a
// real pipe does; this file's PipeStream can.
public class DapSessionRunningBreakpointResyncTests
{
    private sealed class TestBreakpoint(int line) : IBreakpoint
    {
        public string FileName => "";
        public int Line { get; set; } = line;
        public string? Condition => null;
        public string? HitCondition => null;
    }

    // A minimal one-directional async pipe — Push enqueues bytes, ReadAsync blocks until at least
    // one is available (real pipe semantics), rather than a MemoryStream's "return 0 once past the
    // end of whatever was written so far" behavior, which DapSession would read as netcoredbg
    // having exited.
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

        public void Kill()
        {
        }

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

    // Waits for InputStream's captured bytes (everything DapSession has written to "netcoredbg" so
    // far) to contain the given substring — polling since the write under test happens on a
    // different async flow than this method's own await chain (SendBreakpointsWhileRunningAsync is
    // deliberately fire-and-forget, see its own comment).
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
    public async Task RequestBreakpointResync_Should_SendSetBreakpointsImmediately_WhileTheDebuggeeIsRunning()
    {
        var breakpoints = new List<IBreakpoint>();
        var process = new FakeChildProcess();
        var options = new DapSessionOptions(
            ProgramPath: "out.dll",
            WorkingDirectory: ".",
            Breakpoints: breakpoints,
            SourceFileExtension: ".cs",
            LineOffset: 0);

        await using var session = new DapSession(
            process, options, outputHandler: null, onBreakpointHit: static _ => { },
            logger: NullLogger<DapSession>.Instance);

        var runTask = session.RunAsync(TestContext.Current.CancellationToken);

        // Handshake: initialize response -> session sends launch; initialized event -> session
        // sends setBreakpoints (empty list here, so no response needed — see SendBreakpointsAsync's
        // own early return) then setExceptionBreakpoints (does need one) then configurationDone.
        process.Output.Push(Frame(new { seq = 1, type = "response", request_seq = 1, command = "initialize", success = true }));
        process.Output.Push(Frame(new { seq = 2, type = "event", @event = "initialized" }));
        await WaitForWrittenRequestAsync(process.Input, "setExceptionBreakpoints");
        process.Output.Push(Frame(new { seq = 3, type = "response", request_seq = 3, command = "setExceptionBreakpoints", success = true }));

        // Nothing else arrives after this — the main loop is now genuinely blocked waiting for the
        // next message, exactly like a script that's running with nothing about to stop it.

        // Act — simulate a gutter breakpoint add: mutate the shared list (what LanguageHostBase.
        // AddBreakpoint does) then notify the session (CompiledDotnetLanguageHostBase.AddBreakpoint).
        breakpoints.Add(new TestBreakpoint(5));
        session.RequestBreakpointResync();

        // Assert — a fresh setBreakpoints request went out immediately, without needing any other
        // "stopped" event first. Before the fix, nothing was ever sent here at all.
        var written = await WaitForWrittenRequestAsync(process.Input, "\"line\":5");
        Assert.Contains("setBreakpoints", written);

        process.Output.Push(Frame(new { seq = 4, type = "event", @event = "exited" }));
        await runTask;
    }
}
