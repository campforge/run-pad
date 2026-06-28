using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DotPad.Build.Core.ProcessRunners;
using DotPad.Languages.CSharp.Debug.Protocol;
using DotPad.Contracts;

namespace DotPad.Languages.CSharp.Debug;

/// <summary>
/// Manages a full VS Code Debug Adapter Protocol session with netcoredbg.
/// Protocol flow: initialize → launch → setBreakpoints → configurationDone → event loop.
/// At each "stopped" event variables and call stack are captured before auto-continuing.
/// </summary>
internal sealed class CSharpDapSession : IAsyncDisposable
{
    private const string ContentLengthHeader = "Content-Length: ";
    private const string TwoCrlf = "\r\n\r\n";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly LocalDebuggerProcess _process;
    private readonly string _programPath;
    private readonly string _workingDirectory;
    private readonly IReadOnlyList<IBreakpoint> _breakpoints;
    private readonly OutputReceivedHandler? _outputHandler;
    private readonly Action<IBreakpointHitContext> _onBreakpointHit;

    private ChannelReader<string> Reader => _process.Reader;
    private int _seq;

    internal CSharpDapSession(
        LocalDebuggerProcess process,
        string programPath,
        string workingDirectory,
        IReadOnlyList<IBreakpoint> breakpoints,
        OutputReceivedHandler? outputHandler,
        Action<IBreakpointHitContext> onBreakpointHit)
    {
        _process = process;
        _programPath = programPath;
        _workingDirectory = workingDirectory;
        _breakpoints = breakpoints;
        _outputHandler = outputHandler;
        _onBreakpointHit = onBreakpointHit;
    }

    internal async Task RunAsync(CancellationToken ct)
    {
        // I/O pumping runs in background until the debugger process exits.
        var ioTask = _process.Start(ct);

        try
        {
            await RunProtocolAsync(ct);
        }
        finally
        {
            await _process.WaitForExit(ct).ConfigureAwait(false);
            try { await ioTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    // -------------------------------------------------------------------------
    // Protocol
    // -------------------------------------------------------------------------

    private async Task RunProtocolAsync(CancellationToken ct)
    {
        await SendAsync(new InitializeRequest
        {
            seq = NextSeq(),
            arguments = new InitializeRequestArguments
            {
                clientID      = "dotpad",
                clientName    = "DotPad",
                adapterID     = "coreclr",
                linesStartAt1 = true,
                columnsStartAt1 = true,
                pathFormat    = "path",
                supportsVariableType = true,
            }
        }, ct);

        bool launchSent = false;

        while (!ct.IsCancellationRequested)
        {
            using var doc = await ReadNextMessageAsync(ct);
            if (doc is null) break;

            var root = doc.RootElement;
            var msgType = GetString(root, "type");

            switch (msgType)
            {
                case "response":
                    if (!launchSent && GetString(root, "command") == "initialize")
                    {
                        await SendAsync(new LaunchRequest
                        {
                            seq = NextSeq(),
                            arguments = new LaunchRequestArguments
                            {
                                program    = _programPath,
                                cwd        = _workingDirectory,
                                stopAtEntry = false,
                                justMyCode  = false,
                            }
                        }, ct);
                        launchSent = true;
                    }
                    break;

                case "event":
                    if (await HandleEventAsync(root, ct))
                        return; // terminated / exited
                    break;
            }
        }
    }

    // Returns true when the session should end.
    private async Task<bool> HandleEventAsync(JsonElement root, CancellationToken ct)
    {
        var evt = GetString(root, "event");

        switch (evt)
        {
            case "initialized":
                await SendBreakpointsAsync(ct);
                await SendAsync(new ConfigurationDoneRequest { seq = NextSeq() }, ct);
                break;

            case "stopped":
                var body     = root.GetProperty("body");
                var threadId = body.TryGetProperty("threadId", out var tid) ? tid.GetInt32() : 1;
                var reason   = GetString(body, "reason");

                if (reason is "breakpoint" or "step" or "pause" or "exception")
                    await HandleStoppedAsync(threadId, ct);
                else
                    await SendContinueAsync(threadId, ct);
                break;

            case "output":
                ForwardOutput(root);
                break;

            case "exited":
            case "terminated":
                return true;
        }

        return false;
    }

    // -------------------------------------------------------------------------
    // Stopped handling: stack trace + variable capture → callback → continue
    // -------------------------------------------------------------------------

    private async Task HandleStoppedAsync(int threadId, CancellationToken ct)
    {
        var frames    = await QueryStackTraceAsync(threadId, ct);
        var variables = frames.Count > 0
            ? await QueryLocalsAsync(frames[0].id, ct)
            : (IReadOnlyList<IVariableInfo>)[];

        var topFrame = frames.Count > 0 ? frames[0] : null;
        var topFile  = topFrame?.source?.path ?? topFrame?.source?.name ?? "";

        IBreakpoint? matched = _breakpoints.FirstOrDefault(bp =>
            string.Equals(bp.FileName, topFile, StringComparison.OrdinalIgnoreCase)
            && bp.Line == topFrame?.line);

        matched ??= _breakpoints.FirstOrDefault()
            ?? new SyntheticBreakpoint(topFile, topFrame?.line ?? 0);

        var context = new DapBreakpointHitContext
        {
            Breakpoint     = matched,
            ThreadId       = threadId,
            LocalVariables = variables,
            CallStack      = frames
                .Select(f => (IStackFrame)new DapStackFrame
                {
                    MethodName = f.name ?? "",
                    FileName   = f.source?.path ?? f.source?.name ?? "",
                    Line       = f.line,
                    Column     = f.column,
                })
                .ToList(),
        };

        _onBreakpointHit(context);

        await SendContinueAsync(threadId, ct);
    }

    // -------------------------------------------------------------------------
    // DAP request helpers (send → read matching response, skip intervening events)
    // -------------------------------------------------------------------------

    private async Task<List<StackFrame>> QueryStackTraceAsync(int threadId, CancellationToken ct)
    {
        await SendAsync(new StackTraceRequest
        {
            seq       = NextSeq(),
            arguments = new StackTraceArguments { threadId = threadId },
        }, ct);

        using var doc = await ReadResponseForCommandAsync("stackTrace", ct);
        if (doc is null) return [];

        var root = doc.RootElement;
        if (!root.TryGetProperty("body", out var body)) return [];
        if (!body.TryGetProperty("stackFrames", out var framesEl)) return [];

        return JsonSerializer.Deserialize<List<StackFrame>>(framesEl.GetRawText(), JsonOpts) ?? [];
    }

    private async Task<IReadOnlyList<IVariableInfo>> QueryLocalsAsync(long frameId, CancellationToken ct)
    {
        // 1. Get scopes.
        await SendAsync(new ScopesRequest
        {
            seq       = NextSeq(),
            arguments = new ScopesArguments { frameId = frameId },
        }, ct);

        using var scopesDoc = await ReadResponseForCommandAsync("scopes", ct);
        if (scopesDoc is null) return [];

        var scopesRoot = scopesDoc.RootElement;
        if (!scopesRoot.TryGetProperty("body", out var sb)) return [];
        if (!sb.TryGetProperty("scopes", out var scopesEl)) return [];

        var scopes     = JsonSerializer.Deserialize<List<Scope>>(scopesEl.GetRawText(), JsonOpts) ?? [];
        var locals     = scopes.FirstOrDefault(s =>
            s.name?.Equals("Locals", StringComparison.OrdinalIgnoreCase) == true);
        var varRef     = locals?.variablesReference ?? 0;
        if (varRef == 0) return [];

        // 2. Get variables in the Locals scope.
        await SendAsync(new VariablesRequest
        {
            seq       = NextSeq(),
            arguments = new VariablesArguments { variablesReference = varRef },
        }, ct);

        using var varsDoc = await ReadResponseForCommandAsync("variables", ct);
        if (varsDoc is null) return [];

        var varsRoot = varsDoc.RootElement;
        if (!varsRoot.TryGetProperty("body", out var vb)) return [];
        if (!vb.TryGetProperty("variables", out var varsEl)) return [];

        var vars = JsonSerializer.Deserialize<List<Variable>>(varsEl.GetRawText(), JsonOpts) ?? [];
        return vars
            .Select(v => (IVariableInfo)new DapVariableInfo
            {
                Name     = v.name ?? "",
                Value    = v.value ?? "",
                TypeName = v.type ?? "",
                HasChildren = v.variablesReference > 0,
            })
            .ToList();
    }

    private async Task SendBreakpointsAsync(CancellationToken ct)
    {
        foreach (var group in _breakpoints.GroupBy(bp => bp.FileName))
        {
            await SendAsync(new SetBreakpointsRequest
            {
                seq = NextSeq(),
                arguments = new SetBreakpointsArguments
                {
                    source      = new Source { path = group.Key, name = Path.GetFileName(group.Key) },
                    breakpoints = group.Select(bp => new SourceBreakpoint(bp.Line)).ToList(),
                },
            }, ct);
        }
    }

    private Task SendContinueAsync(int threadId, CancellationToken ct) =>
        SendAsync(new ContinueRequest
        {
            seq       = NextSeq(),
            arguments = new ContinueArguments { threadId = threadId },
        }, ct);

    private void ForwardOutput(JsonElement root)
    {
        if (!root.TryGetProperty("body", out var body)) return;
        var text     = GetString(body, "output") ?? "";
        var category = GetString(body, "category") ?? "stdout";
        var kind     = category == "stderr" ? OutputKind.StandardError : OutputKind.StandardOutput;
        _outputHandler?.Invoke(text, kind);
    }

    // -------------------------------------------------------------------------
    // Send / Receive
    // -------------------------------------------------------------------------

    private async Task SendAsync(object request, CancellationToken ct)
    {
        var json    = JsonSerializer.Serialize(request, request.GetType(), JsonOpts);
        var byteLen = Encoding.UTF8.GetByteCount(json);
        var framed  = $"{ContentLengthHeader}{byteLen}{TwoCrlf}{json}";
        await _process.InputWriter.WriteAsync(framed, ct);
    }

    /// <summary>
    /// Reads messages from the channel until the response for <paramref name="command"/> arrives.
    /// Output events are forwarded; exited/terminated cause a null return.
    /// </summary>
    private async Task<JsonDocument?> ReadResponseForCommandAsync(string command, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var doc = await ReadNextMessageAsync(ct);
            if (doc is null) return null;

            var root    = doc.RootElement;
            var msgType = GetString(root, "type");

            if (msgType == "response" && GetString(root, "command") == command)
                return doc; // caller owns disposal

            if (msgType == "event")
            {
                var evt = GetString(root, "event");
                if (evt == "output") ForwardOutput(root);
                if (evt is "exited" or "terminated") { doc.Dispose(); return null; }
            }

            doc.Dispose();
        }

        return null;
    }

    /// <summary>
    /// Reads one Content-Length framed DAP message from the output channel.
    /// netcoredbg sends JSON bodies as single lines; multi-line is handled
    /// by accumulating until the declared byte count is reached.
    /// </summary>
    private async Task<JsonDocument?> ReadNextMessageAsync(CancellationToken ct)
    {
        try
        {
            while (await Reader.WaitToReadAsync(ct))
            {
                var line = await Reader.ReadAsync(ct);

                if (!line.StartsWith(ContentLengthHeader, StringComparison.Ordinal))
                    continue;

                if (!int.TryParse(line.AsSpan(ContentLengthHeader.Length).Trim(), out var length))
                    continue;

                // Discard empty separator line.
                if (!await Reader.WaitToReadAsync(ct)) return null;
                _ = await Reader.ReadAsync(ct);

                // Accumulate body lines until we have the declared byte count.
                var body = new StringBuilder(length);
                while (Encoding.UTF8.GetByteCount(body.ToString()) < length)
                {
                    if (!await Reader.WaitToReadAsync(ct)) break;
                    var bodyLine = await Reader.ReadAsync(ct);
                    if (body.Length > 0) body.Append('\n');
                    body.Append(bodyLine);
                }

                var json = body.ToString();
                if (!string.IsNullOrWhiteSpace(json))
                    return JsonDocument.Parse(json);
            }
        }
        catch (OperationCanceledException) { }

        return null;
    }

    // -------------------------------------------------------------------------
    // Utilities
    // -------------------------------------------------------------------------

    private static string? GetString(JsonElement el, string property) =>
        el.TryGetProperty(property, out var p) ? p.GetString() : null;

    private int NextSeq() => Interlocked.Increment(ref _seq);

    public async ValueTask DisposeAsync()
    {
        await _process.Close(CancellationToken.None).ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
    }
}
