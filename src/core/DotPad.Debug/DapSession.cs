using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotPad.Build.Core.ProcessRunners;
using DotPad.Contracts;
using DotPad.Contracts.Models;
using DotPad.Debug.Protocol;
using Microsoft.Extensions.Logging;

namespace DotPad.Debug;

/// <summary>
/// Full VS Code Debug Adapter Protocol session with netcoredbg. Shared by every compiled-.NET
/// language host (C#, VB, ...) — DAP/netcoredbg debugging works against compiled IL + PDB and
/// doesn't care what source language produced them; only <paramref name="sourceFileExtension"/>
/// (the ctor parameter) needs to vary per language, so the compiled source file's fallback path can
/// be reconstructed correctly.
///
/// Message framing (from reference implementation VSCodeLocalDebuggerClient):
///   Send:    Content-Length: {UTF-8-byte-count}\r\n\r\n{JSON-body-no-trailing-newline}
///   Receive: read header byte-by-byte until "\r\n\r\n", parse Content-Length, read exactly N bytes.
///
/// Interactive model: on each "stopped" event the session pauses and calls _onBreakpointHit.
/// The caller must invoke context.Resume(action) to unblock execution.
/// </summary>
public sealed class DapSession : IDapSession
{
    private const string ContentLengthPrefix = "Content-Length: ";

    // How long to wait for netcoredbg to acknowledge "disconnect" (and then actually exit) after the
    // debuggee finished, before giving up and force-killing it — ANALYSIS.md #94 (named, not a bare
    // literal, since both used to be unexplained "2" seconds).
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DisconnectWaitForExitTimeout = TimeSpan.FromSeconds(2);

    // Truncation length for DAP request/response bodies in debug-level logs — long enough to show
    // which command/event a log line is about, short enough not to dump entire variable trees.
    private const int LogSnippetLength = 200;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IChildProcess _process;
    private readonly string _programPath;
    private readonly string _workingDirectory;
    private readonly IReadOnlyList<IBreakpoint> _breakpoints;
    private readonly string _sourceFileExtension;
    private readonly OutputReceivedHandler? _outputHandler;
    private readonly Action<IBreakpointHitContext> _onBreakpointHit;
    private readonly Action<IBreakpoint, bool, string?>? _onBreakpointVerified;
    private readonly ILogger<DapSession> _logger;
    private readonly bool _breakOnAllExceptions;
    private readonly bool _breakOnUncaughtExceptions;
    private readonly bool _justMyCode;

    // Serializes writes to stdin so PauseAsync from the UI thread can't interleave.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Serializes UI-triggered send+read round trips (lazy variable children, watch evaluation) that
    // happen while paused, outside HandleStoppedAsync's own sequential await chain — safe only
    // because the main read loop is itself idle (blocked on the resume signal) during that window,
    // but two such calls firing concurrently (e.g. expanding a tree node while evaluating a watch)
    // would still race each other on the same stream without this.
    private readonly SemaphoreSlim _requestResponseLock = new(1, 1);

    // Top stack frame id from the most recent "stopped" event — evaluate/watch requests run in this
    // frame's scope.
    private long _currentFrameId;

    // Lines prepended by the compiler before the user's first line.
    // Sent breakpoints: user_line + _lineOffset → compiled_line.
    // Received frame.line: compiled_line - _lineOffset → user_line.
    private readonly int _lineOffset;

    // Run-to-cursor one-shot breakpoints (user line numbers).
    private readonly HashSet<int> _runToCursorLines = [];

    // Set by RequestBreakpointResync (any thread) when a gutter breakpoint changed during an active
    // session; consumed the next time HandleStoppedAsync reaches its pre-resume resync check.
    private volatile bool _breakpointResyncPending;

    // Stored after first SendBreakpointsAsync so ResendBreakpointsAsync can reuse them.
    private string? _sourceFile;
    private string? _sourceName;

    private int _seq;
    private volatile int _activeThreadId;
    private volatile bool _isPaused;
    private volatile bool _processExited;

    public DapSession(
        IChildProcess process,
        DapSessionOptions options,
        OutputReceivedHandler? outputHandler,
        Action<IBreakpointHitContext> onBreakpointHit,
        ILogger<DapSession> logger,
        Action<IBreakpoint, bool, string?>? onBreakpointVerified = null)
    {
        _process = process;
        _programPath = options.ProgramPath;
        _workingDirectory = options.WorkingDirectory;
        _breakpoints = options.Breakpoints;
        _sourceFileExtension = options.SourceFileExtension;
        _outputHandler = outputHandler;
        _onBreakpointHit = onBreakpointHit;
        _lineOffset = options.LineOffset;
        _logger = logger;
        _breakOnAllExceptions = options.BreakOnAllExceptions;
        _breakOnUncaughtExceptions = options.BreakOnUncaughtExceptions;
        _onBreakpointVerified = onBreakpointVerified;
        _justMyCode = options.JustMyCode;
    }

    // ─── External control ────────────────────────────────────────────────────

    public async Task PauseAsync(CancellationToken ct)
    {
        if (_isPaused) return;
        await SendAsync(new PauseRequest
        {
            seq = NextSeq(),
            arguments = new PauseArguments { threadId = _activeThreadId },
        }, ct);
    }

    public void RequestBreakpointResync() => _breakpointResyncPending = true;

    // ─── Session entry point ─────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        // Failing fast here with a clear message beats the confusing exception (e.g.
        // InvalidOperationException from a stream never redirected) that would otherwise surface
        // later out of RunProtocolAsync's first read/write attempt (ANALYSIS.md #92).
        if (!_process.Start())
            throw new InvalidOperationException("Failed to start the debugger process.");
        var stderrTask = DrainStderrAsync(ct);
        try
        {
            await RunProtocolAsync(ct);
        }
        finally
        {
            if (_processExited)
            {
                // Debuggee finished, but netcoredbg is still running waiting for disconnect.
                try
                {
                    using var dCts = new CancellationTokenSource(DisconnectTimeout);
                    await SendAsync(new DisconnectRequest
                    {
                        seq = NextSeq(),
                        arguments = new DisconnectArguments { terminateDebuggee = true },
                    }, dCts.Token);
                    using var wCts = new CancellationTokenSource(DisconnectWaitForExitTimeout);
                    await _process.WaitForExitAsync(wCts.Token);
                }
                catch { }
            }

            // Kill if still running (forced cancel or disconnect timeout).
            _process.Kill();
            try { await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { }
            try { await stderrTask.ConfigureAwait(false); }
            catch { }
        }
    }

    private async Task DrainStderrAsync(CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                _logger.LogDebug("[dbg] {Line}", line);
                _outputHandler?.Invoke($"[dbg] {line}", OutputKind.StandardError);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ─── Protocol main loop ──────────────────────────────────────────────────

    private async Task RunProtocolAsync(CancellationToken ct)
    {
        await SendAsync(new InitializeRequest
        {
            seq = NextSeq(),
            arguments = new InitializeRequestArguments
            {
                clientID = "dotpad",
                clientName = "DotPad",
                adapterID = "coreclr",
                linesStartAt1 = true,
                columnsStartAt1 = true,
                pathFormat = "path",
                supportsVariableType = true,
            }
        }, ct);

        bool launchSent = false;

        while (!ct.IsCancellationRequested)
        {
            using var doc = await ReadMessageAsync(ct);
            if (doc is null) break;

            var root = doc.RootElement;
            var msgType = GetStr(root, "type");

            if (msgType == "response" && !launchSent && GetStr(root, "command") == "initialize")
            {
                await SendAsync(new LaunchRequest
                {
                    seq = NextSeq(),
                    arguments = new LaunchRequestArguments
                    {
                        program = _programPath,
                        cwd = _workingDirectory,
                        stopAtEntry = false,
                        justMyCode = _justMyCode,
                        console = "internalConsole",
                    }
                }, ct);
                launchSent = true;
            }
            else if (msgType == "event")
            {
                if (await HandleEventAsync(root, ct))
                    return;
            }
        }
    }

    private async Task<bool> HandleEventAsync(JsonElement root, CancellationToken ct)
    {
        switch (GetStr(root, "event"))
        {
            case "initialized":
                await SendBreakpointsAsync(ct);
                await SendExceptionBreakpointsAsync(ct);
                await SendAsync(new ConfigurationDoneRequest { seq = NextSeq() }, ct);
                break;

            case "stopped":
                var body = root.GetProperty("body");
                var threadId = body.TryGetProperty("threadId", out var tid) ? tid.GetInt32() : 1;
                var reason = GetStr(body, "reason");
                _activeThreadId = threadId;
                _isPaused = true;
                if (reason is "breakpoint" or "step" or "pause" or "exception" or "entry")
                    await HandleStoppedAsync(threadId, ct);
                else
                    await SendContinueAsync(threadId, ct);
                _isPaused = false;
                break;

            case "output":
                ForwardOutput(root);
                break;

            // netcoredbg answers the initial setBreakpoints response with verified=false
            // ("pending, will be resolved when debugging starts") before the target module is even
            // loaded — that response alone would make every breakpoint look permanently unverified.
            // The real verdict arrives later as this unsolicited event once the module loads.
            case "breakpoint":
                HandleBreakpointEvent(root);
                break;

            case "exited":
            case "terminated":
                _processExited = true;
                return true;
        }

        return false;
    }

    // ─── Stopped: gather context → notify caller → wait for Resume ───────────

    private async Task HandleStoppedAsync(int threadId, CancellationToken ct)
    {
        var threads = await QueryThreadsAsync(ct);
        var frames = await QueryStackTraceAsync(threadId, ct);
        var vars = frames.Count > 0
            ? await QueryAllScopesAsync(frames[0].id, ct)
            : (IReadOnlyList<IVariableInfo>)[];

        var topFrame = frames.Count > 0 ? frames[0] : null;
        var topFrameUserLine = (topFrame?.line ?? 0) - _lineOffset;
        var topFrameSourcePath = topFrame?.source?.path;
        // Matching on Line alone would misattribute a hit to the wrong breakpoint if two source
        // files coincidentally used the same line number (won't happen with a single script file,
        // but IBreakpoint.FileName is now populated, so verify it when both sides have one).
        IBreakpoint matched =
            _breakpoints.FirstOrDefault(bp => bp.Line == topFrameUserLine
                && (string.IsNullOrEmpty(bp.FileName) || topFrameSourcePath is null
                    || string.Equals(bp.FileName, topFrameSourcePath, StringComparison.OrdinalIgnoreCase)))
            ?? new SyntheticBreakpoint(topFrameSourcePath ?? "", topFrameUserLine);

        bool wasRunToCursor = _runToCursorLines.Remove(topFrameUserLine);
        _currentFrameId = topFrame?.id ?? 0;

        var context = new DapBreakpointHitContext
        {
            Breakpoint = matched,
            ThreadId = threadId,
            LocalVariables = vars,
            GetChildrenFunc = FetchChildrenAsync,
            RunToCursorFunc = RegisterRunToCursorAsync,
            EvaluateFunc = EvaluateAsync,
            CallStack = frames.Select(f => (IStackFrame)new DapStackFrame
            {
                MethodName = f.name ?? "",
                FileName = f.source?.path ?? f.source?.name ?? "",
                Line = f.line - _lineOffset,
                Column = f.column,
            }).ToList(),
            Threads = threads.Select(t => (IThreadInfo)new DapThreadInfo
            {
                Id = t.id,
                Name = t.name ?? $"Thread {t.id}",
            }).ToList(),
        };

        _onBreakpointHit(context);

        // Block here until the UI calls context.Resume(action).
        var action = await context.WaitForResumeAsync(ct);

        // Resend breakpoints if a run-to-cursor BP was consumed, a new one was registered, or a
        // gutter breakpoint was added/removed while paused here (RequestBreakpointResync).
        if (wasRunToCursor || _runToCursorLines.Count > 0 || _breakpointResyncPending)
        {
            _breakpointResyncPending = false;
            await ResendBreakpointsAsync(ct);
        }

        switch (action)
        {
            case DebugAction.Continue:
                await SendContinueAsync(threadId, ct);
                break;
            case DebugAction.StepOver:
                await SendAsync(new NextRequest { seq = NextSeq(), arguments = new NextArguments { threadId = threadId } }, ct);
                break;
            case DebugAction.StepIn:
                await SendAsync(new StepInRequest { seq = NextSeq(), arguments = new StepInArguments { threadId = threadId } }, ct);
                break;
            case DebugAction.StepOut:
                await SendAsync(new StepOutRequest { seq = NextSeq(), arguments = new StepOutArguments { threadId = threadId } }, ct);
                break;
        }
    }

    // Body shape: { reason: "changed"|"new"|"removed", breakpoint: { verified, line, message, ... } }.
    // "line" is in compiled-file coordinates, same as what SendBreakpointsAsync sent.
    private void HandleBreakpointEvent(JsonElement root)
    {
        if (!root.TryGetProperty("body", out var body)) return;
        if (!body.TryGetProperty("breakpoint", out var bpEl)) return;
        if (!bpEl.TryGetProperty("line", out var lineEl)) return;

        var verified = bpEl.TryGetProperty("verified", out var v) && v.GetBoolean();
        var msg = bpEl.TryGetProperty("message", out var m) ? m.GetString() : null;
        var userLine = lineEl.GetInt32() - _lineOffset;
        var sourcePath = bpEl.TryGetProperty("source", out var srcEl) && srcEl.TryGetProperty("path", out var pathEl)
            ? pathEl.GetString()
            : null;

        // Same tolerant FileName match as HandleStoppedAsync (ANALYSIS.md #92) — matching on Line
        // alone would misattribute this event to the wrong breakpoint if two source files
        // coincidentally used the same line number.
        var matched = _breakpoints.FirstOrDefault(bp => bp.Line == userLine
            && (string.IsNullOrEmpty(bp.FileName) || sourcePath is null
                || string.Equals(bp.FileName, sourcePath, StringComparison.OrdinalIgnoreCase)));
        if (matched is null) return;

        _logger.LogDebug("[dap bp] (event) verified={Verified} line={Line}{Msg}", verified, userLine, msg is not null ? $" msg={msg}" : "");
        _onBreakpointVerified?.Invoke(matched, verified, msg);
    }

    private Task RegisterRunToCursorAsync(int userLine, CancellationToken _)
    {
        _runToCursorLines.Add(userLine);
        return Task.CompletedTask;
    }

    // ─── DAP request helpers ─────────────────────────────────────────────────

    private async Task<List<DapThread>> QueryThreadsAsync(CancellationToken ct)
    {
        await SendAsync(new ThreadsRequest { seq = NextSeq() }, ct);
        using var doc = await ReadResponseForCommandAsync("threads", ct);
        if (doc is null) return [];

        var root = doc.RootElement;
        if (!root.TryGetProperty("body", out var body)) return [];
        if (!body.TryGetProperty("threads", out var el)) return [];
        return JsonSerializer.Deserialize<List<DapThread>>(el.GetRawText(), _jsonOpts) ?? [];
    }

    private async Task<List<StackFrame>> QueryStackTraceAsync(int threadId, CancellationToken ct)
    {
        await SendAsync(new StackTraceRequest
        {
            seq = NextSeq(),
            arguments = new StackTraceArguments { threadId = threadId },
        }, ct);
        using var doc = await ReadResponseForCommandAsync("stackTrace", ct);
        if (doc is null) return [];

        var root = doc.RootElement;
        if (!root.TryGetProperty("body", out var body)) return [];
        if (!body.TryGetProperty("stackFrames", out var el)) return [];
        return JsonSerializer.Deserialize<List<StackFrame>>(el.GetRawText(), _jsonOpts) ?? [];
    }

    private async Task<IReadOnlyList<IVariableInfo>> QueryAllScopesAsync(long frameId, CancellationToken ct)
    {
        await SendAsync(new ScopesRequest
        {
            seq = NextSeq(),
            arguments = new ScopesArguments { frameId = frameId },
        }, ct);
        using var scopesDoc = await ReadResponseForCommandAsync("scopes", ct);
        if (scopesDoc is null) return [];

        var scopesRoot = scopesDoc.RootElement;
        if (!scopesRoot.TryGetProperty("body", out var scopesBody)) return [];
        if (!scopesBody.TryGetProperty("scopes", out var scopesEl)) return [];

        var scopes = JsonSerializer.Deserialize<List<Scope>>(scopesEl.GetRawText(), _jsonOpts) ?? [];
        var usableScopes = scopes.Where(s => s.expensive != true && s.variablesReference is > 0).ToList();
        if (usableScopes.Count == 0) return [];

        var result = new List<IVariableInfo>();
        bool multipleScopes = usableScopes.Count > 1;

        foreach (var scope in usableScopes)
        {
            if (multipleScopes)
            {
                result.Add(new DapVariableInfo
                {
                    Name = $"── {scope.name} ──",
                    Value = "",
                    TypeName = "",
                    HasChildren = false,
                    VariablesReference = 0,
                });
            }

            await SendAsync(new VariablesRequest
            {
                seq = NextSeq(),
                arguments = new VariablesArguments { variablesReference = scope.variablesReference!.Value },
            }, ct);
            using var varsDoc = await ReadResponseForCommandAsync("variables", ct);
            if (varsDoc is null) continue;

            var varsRoot = varsDoc.RootElement;
            if (!varsRoot.TryGetProperty("body", out var varsBody)) continue;
            if (!varsBody.TryGetProperty("variables", out var varsEl)) continue;

            var vars = JsonSerializer.Deserialize<List<Variable>>(varsEl.GetRawText(), _jsonOpts) ?? [];
            result.AddRange(vars.Select(v => (IVariableInfo)new DapVariableInfo
            {
                Name = v.name ?? "",
                Value = v.value ?? "",
                TypeName = v.type ?? "",
                HasChildren = v.variablesReference > 0,
                VariablesReference = v.variablesReference,
            }));
        }

        return result;
    }

    private async Task<IReadOnlyList<IVariableInfo>> FetchChildrenAsync(int variablesReference, CancellationToken ct)
    {
        await _requestResponseLock.WaitAsync(ct);
        try
        {
            await SendAsync(new VariablesRequest
            {
                seq = NextSeq(),
                arguments = new VariablesArguments { variablesReference = variablesReference },
            }, ct);
            using var doc = await ReadResponseForCommandAsync("variables", ct);
            if (doc is null) return [];

            var root = doc.RootElement;
            if (!root.TryGetProperty("body", out var body)) return [];
            if (!body.TryGetProperty("variables", out var el)) return [];

            var vars = JsonSerializer.Deserialize<List<Variable>>(el.GetRawText(), _jsonOpts) ?? [];
            return vars.Select(v => (IVariableInfo)new DapVariableInfo
            {
                Name = v.name ?? "",
                Value = v.value ?? "",
                TypeName = v.type ?? "",
                HasChildren = v.variablesReference > 0,
                VariablesReference = v.variablesReference,
            }).ToList();
        }
        finally { _requestResponseLock.Release(); }
    }

    // Backs IBreakpointHitContext.EvaluateAsync (Watch panel) — DAP "evaluate" in the current
    // top-frame's scope. Guarded by _requestResponseLock like FetchChildrenAsync since both can be
    // triggered by the UI at arbitrary times while paused.
    private async Task<IVariableInfo?> EvaluateAsync(string expression, CancellationToken ct)
    {
        if (!_isPaused) return null;

        await _requestResponseLock.WaitAsync(ct);
        try
        {
            await SendAsync(new EvaluateRequest
            {
                seq = NextSeq(),
                arguments = new EvaluateArguments { expression = expression, frameId = _currentFrameId, context = "watch" },
            }, ct);
            using var doc = await ReadResponseForCommandAsync("evaluate", ct);
            if (doc is null) return null;

            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            if (!success)
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                return new DapVariableInfo
                {
                    Name = expression,
                    Value = message ?? "<error>",
                    TypeName = "",
                    HasChildren = false,
                    VariablesReference = 0,
                };
            }

            if (!root.TryGetProperty("body", out var body)) return null;
            var result = body.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
            var type = body.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var varRef = body.TryGetProperty("variablesReference", out var vr) ? vr.GetInt32() : 0;
            return new DapVariableInfo
            {
                Name = expression,
                Value = result,
                TypeName = type,
                HasChildren = varRef > 0,
                VariablesReference = varRef,
            };
        }
        finally { _requestResponseLock.Release(); }
    }

    private async Task SendBreakpointsAsync(CancellationToken ct)
    {
        // Prefer the path the breakpoints were actually set against (IBreakpoint.FileName). Fall back
        // to the well-known compiled path when there are no breakpoints yet — e.g. before the first
        // Run to Cursor request arrives, ResendBreakpointsAsync still needs somewhere to send it.
        // The compiler writes source to _workingDirectory/{name}.{sourceFileExtension}
        // but the binary goes to _workingDirectory/bin/{name}.dll.
        var fallback = Path.Combine(_workingDirectory,
            Path.GetFileNameWithoutExtension(_programPath) + _sourceFileExtension);
        _sourceFile = _breakpoints.FirstOrDefault()?.FileName is { Length: > 0 } fromBreakpoint
            ? fromBreakpoint
            : fallback;
        _sourceName = Path.GetFileName(_sourceFile);

        if (_breakpoints.Count == 0) return;

        _logger.LogDebug("[dap bp] source path: {SourceFile}, lineOffset: {Offset}", _sourceFile, _lineOffset);
        foreach (var bp in _breakpoints)
            _logger.LogDebug("[dap bp] user line {UserLine} → compiled line {CompiledLine}", bp.Line, bp.Line + _lineOffset);

        await SendAsync(new SetBreakpointsRequest
        {
            seq = NextSeq(),
            arguments = new SetBreakpointsArguments
            {
                source = new Source { path = _sourceFile, name = _sourceName },
                breakpoints = _breakpoints
                    .Select(bp => new SourceBreakpoint(bp.Line + _lineOffset, bp.Condition, bp.HitCondition))
                    .ToList(),
            },
        }, ct);

        // Read the response and report verified/message back per breakpoint (DAP guarantees the
        // response array is in the same order as the request array, so zip by index with _breakpoints).
        using var doc = await ReadResponseForCommandAsync("setBreakpoints", ct);
        ReportVerification(doc, _breakpoints);
    }

    private void ReportVerification(JsonDocument? doc, IReadOnlyList<IBreakpoint> requested)
    {
        if (doc is null) return;
        var root = doc.RootElement;
        if (!root.TryGetProperty("body", out var body) || !body.TryGetProperty("breakpoints", out var bps)) return;

        int i = 0;
        foreach (var bp in bps.EnumerateArray())
        {
            var verified = bp.TryGetProperty("verified", out var v) && v.GetBoolean();
            var line = bp.TryGetProperty("line", out var l) ? l.GetInt32() : -1;
            var msg = bp.TryGetProperty("message", out var m) ? m.GetString() : null;
            _logger.LogDebug("[dap bp] verified={Verified} line={Line}{Msg}", verified, line, msg is not null ? $" msg={msg}" : "");

            if (i < requested.Count)
                _onBreakpointVerified?.Invoke(requested[i], verified, msg);
            i++;
        }
    }

    private async Task ResendBreakpointsAsync(CancellationToken ct)
    {
        if (_sourceFile is null || _sourceName is null) return;

        var allBps = _breakpoints
            .Select(bp => new SourceBreakpoint(bp.Line + _lineOffset, bp.Condition, bp.HitCondition))
            .Concat(_runToCursorLines.Select(l => new SourceBreakpoint(l + _lineOffset)))
            .ToList();

        await SendAsync(new SetBreakpointsRequest
        {
            seq = NextSeq(),
            arguments = new SetBreakpointsArguments
            {
                source = new Source { path = _sourceFile, name = _sourceName },
                breakpoints = allBps,
            },
        }, ct);
        // Only the first _breakpoints.Count entries correspond to real IBreakpoint objects — the rest
        // are synthetic run-to-cursor lines with nothing in the margin to report back to.
        using var doc = await ReadResponseForCommandAsync("setBreakpoints", ct);
        ReportVerification(doc, _breakpoints);
    }

    private async Task SendExceptionBreakpointsAsync(CancellationToken ct)
    {
        var filters = new List<string>();
        if (_breakOnAllExceptions) filters.Add("all");
        else if (_breakOnUncaughtExceptions) filters.Add("unhandled");

        await SendAsync(new SetExceptionBreakpointsRequest
        {
            seq = NextSeq(),
            arguments = new SetExceptionBreakpointsArguments { filters = filters },
        }, ct);
        using var _ = await ReadResponseForCommandAsync("setExceptionBreakpoints", ct);
    }

    private Task SendContinueAsync(int threadId, CancellationToken ct) =>
        SendAsync(new ContinueRequest
        {
            seq = NextSeq(),
            arguments = new ContinueArguments { threadId = threadId },
        }, ct);

    // netcoredbg splits long stdout lines across multiple DAP output events.
    // Buffer per-category and only invoke the handler on complete \n-terminated lines.
    private readonly StringBuilder _stdoutBuf = new();
    private readonly StringBuilder _stderrBuf = new();

    private void ForwardOutput(JsonElement root)
    {
        if (!root.TryGetProperty("body", out var body)) return;
        var text = GetStr(body, "output") ?? "";
        var category = GetStr(body, "category") ?? "stdout";
        var kind = category == "stderr" ? OutputKind.StandardError : OutputKind.StandardOutput;
        var buf = category == "stderr" ? _stderrBuf : _stdoutBuf;

        buf.Append(text);

        var content = buf.ToString();
        var start = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n') continue;
            var line = content.Substring(start, i - start).TrimEnd('\r');
            // A blank line (e.g. a bare Console.WriteLine()) is real output — dropping it here made
            // debug runs lose blank lines that a non-debug run (DrainReaderAsync, which forwards
            // every ReadLineAsync result unconditionally) keeps (ANALYSIS.md #92).
            _outputHandler?.Invoke(line, kind);
            start = i + 1;
        }
        buf.Clear();
        if (start < content.Length)
            buf.Append(content, start, content.Length - start);
    }

    public void FlushOutputBuffers()
    {
        if (_stdoutBuf.Length > 0)
        {
            _outputHandler?.Invoke(_stdoutBuf.ToString(), OutputKind.StandardOutput);
            _stdoutBuf.Clear();
        }
        if (_stderrBuf.Length > 0)
        {
            _outputHandler?.Invoke(_stderrBuf.ToString(), OutputKind.StandardError);
            _stderrBuf.Clear();
        }
    }

    // ─── Send ────────────────────────────────────────────────────────────────

    private async Task SendAsync(object request, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(request, request.GetType(), _jsonOpts);
        _logger.LogDebug("[dap →] {Snippet}", json[..Math.Min(LogSnippetLength, json.Length)]);
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var message = new byte[header.Length + body.Length];
        header.CopyTo(message, 0);
        body.CopyTo(message, header.Length);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.InputStream.WriteAsync(message, ct).ConfigureAwait(false);
            await _process.InputStream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    // ─── Receive ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads messages until the response for <paramref name="command"/> arrives.
    /// Output events are forwarded; exited/terminated cause a null return.
    /// </summary>
    private async Task<JsonDocument?> ReadResponseForCommandAsync(string command, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var doc = await ReadMessageAsync(ct);
            if (doc is null) return null;

            var root = doc.RootElement;
            var msgType = GetStr(root, "type");

            if (msgType == "response" && GetStr(root, "command") == command)
                return doc;

            if (msgType == "event")
            {
                var evt = GetStr(root, "event");
                if (evt == "output") ForwardOutput(root);
                // The "breakpoint" (changed) event can legitimately arrive while we're blocked here
                // waiting on some other command's response (e.g. during HandleStoppedAsync's
                // threads/stackTrace/variables round-trips) — without this it would be silently
                // dropped and the margin would never learn the debugger's real verified/message verdict.
                else if (evt == "breakpoint") HandleBreakpointEvent(root);
                if (evt is "exited" or "terminated") { doc.Dispose(); return null; }
            }

            doc.Dispose();
        }

        return null;
    }

    /// <summary>
    /// Reads one Content-Length-framed DAP message from the raw stdout stream.
    /// Algorithm mirrors VSCodeLocalDebuggerClient.ReadData() from the netcoredbg test suite.
    /// </summary>
    private async Task<JsonDocument?> ReadMessageAsync(CancellationToken ct)
    {
        var stream = _process.OutputStream;
        var singleByte = new byte[1];
        var headerBuf = new List<byte>(64);

        try
        {
            // 1. Read byte-by-byte until we see the \r\n\r\n separator.
            while (true)
            {
                int n = await stream.ReadAsync(singleByte.AsMemory(0, 1), ct).ConfigureAwait(false);
                if (n == 0) return null; // EOF — debugger exited

                headerBuf.Add(singleByte[0]);

                int len = headerBuf.Count;
                if (len >= 4
                    && headerBuf[len - 4] == '\r'
                    && headerBuf[len - 3] == '\n'
                    && headerBuf[len - 2] == '\r'
                    && headerBuf[len - 1] == '\n')
                    break;
            }

            // 2. Parse "Content-Length: N" from the header.
            var headerStr = Encoding.ASCII.GetString(headerBuf.ToArray());
            int clIdx = headerStr.IndexOf(ContentLengthPrefix, StringComparison.OrdinalIgnoreCase);
            if (clIdx < 0) return null;

            int crIdx = headerStr.IndexOf('\r', clIdx + ContentLengthPrefix.Length);
            if (crIdx < 0) return null;

            if (!int.TryParse(
                    headerStr.AsSpan(clIdx + ContentLengthPrefix.Length,
                                     crIdx - clIdx - ContentLengthPrefix.Length),
                    out int contentLength))
                return null;

            // 3. Read exactly contentLength bytes for the JSON body.
            var bodyBytes = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = await stream.ReadAsync(bodyBytes.AsMemory(read, contentLength - read), ct).ConfigureAwait(false);
                if (n == 0) return null;
                read += n;
            }

            // 4. Parse JSON — decode only the exact N bytes (no null terminator).
            try
            {
                var doc = JsonDocument.Parse(bodyBytes.AsMemory(0, contentLength));
                var snippet = Encoding.UTF8.GetString(bodyBytes, 0, Math.Min(LogSnippetLength, contentLength));
                _logger.LogDebug("[dap ←] {Snippet}", snippet);
                return doc;
            }
            catch (JsonException ex)
            {
                var snippet = Encoding.UTF8.GetString(bodyBytes, 0, Math.Min(LogSnippetLength, contentLength));
                _logger.LogWarning(ex, "DAP parse error | raw: {Raw}", snippet);
                _outputHandler?.Invoke($"[dbg parse error] {ex.Message} | raw: {snippet}", OutputKind.StandardError);
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // ─── Utilities ────────────────────────────────────────────────────────────

    private static string? GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var p) ? p.GetString() : null;

    private int NextSeq() => Interlocked.Increment(ref _seq);

    public async ValueTask DisposeAsync()
    {
        // RunAsync.finally already sent disconnect/kill — just release resources.
        await _process.DisposeAsync().ConfigureAwait(false);
    }
}
