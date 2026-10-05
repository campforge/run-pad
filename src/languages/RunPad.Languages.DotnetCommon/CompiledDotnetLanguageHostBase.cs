using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RunPad.Build.Core.Models;
using RunPad.Build.Core.ProcessRunners;
using RunPad.Contracts;
using RunPad.Contracts.Models;
using RunPad.Debug;
using RunPad.Format.RPad;
using RunPad.Languages.DotnetCommon.Adapters;
using RunPad.Languages.DotnetCommon.Compiler;

namespace RunPad.Languages.DotnetCommon;

/// <summary>
/// Builds the <see cref="IDotnetProcessCompiler"/> a <see cref="CompiledDotnetLanguageHostBase"/> uses
/// to compile a given run — injectable seam (ANALYSIS.md #88) so tests can substitute a fake compiler
/// instead of always shelling out to a real <c>dotnet build</c>.
/// </summary>
public delegate IDotnetProcessCompiler DotnetProcessCompilerFactory(
    DotnetLanguageProjectProfile profile, CompiledDotnetLanguageHostOptions options, ILoggerFactory loggerFactory);

/// <summary>
/// Builds the <see cref="IChildProcess"/> a <see cref="CompiledDotnetLanguageHostBase"/> runs a compiled
/// assembly in — injectable seam (ANALYSIS.md #88 remainder), mirroring <see cref="DotnetProcessCompilerFactory"/>,
/// so tests can substitute a fake process instead of always shelling out to <c>dotnet</c>.
/// </summary>
public delegate IChildProcess ExecutionChildProcessFactory(
    ExecutionOptions options, string assemblyPath, string parentPid, string? currentQueryPath);

/// <summary>
/// Builds the <see cref="IChildProcess"/> a <see cref="CompiledDotnetLanguageHostBase"/> runs netcoredbg
/// in for a debug session — same rationale as <see cref="ExecutionChildProcessFactory"/>.
/// </summary>
public delegate IChildProcess DebuggerChildProcessFactory(ExecutionOptions options);

/// <summary>
/// Shared compile/run/debug orchestration for every .NET language compiled the same way (via
/// <c>dotnet build</c> against a throwaway single-file project, with debugging over DAP against
/// netcoredbg) — was duplicated near-verbatim between CSharpLanguageHost and VbLanguageHost before
/// this base class existed (ANALYSIS.md #86). A concrete language host only needs to supply
/// <see cref="Language"/>/<see cref="ILanguageHost.Capabilities"/> (from <see cref="LanguageHostBase"/>),
/// <see cref="ProjectProfile"/>, <see cref="ApplyKindTransform"/> and <see cref="CalcKindOffset"/> —
/// see CSharpLanguageHost/VbLanguageHost for how small that makes each one.
/// </summary>
public abstract class CompiledDotnetLanguageHostBase : LanguageHostBase
{
    // New format: Record Separator + base64 — see RunPad.Runtime.ObjectExtensions.DumpLinePrefixChar.
    private const char RsPrefix = '\x1e';

    // ASCII Unit Separator + base64 JSON — see RunPad.Runtime.Util.InputRequestPrefixChar.
    private const char InputRequestPrefix = '\x1f';

    private readonly CompiledDotnetLanguageHostOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DotnetProcessCompilerFactory _compilerFactory;
    private readonly ExecutionChildProcessFactory _executionProcessFactory;
    private readonly DebuggerChildProcessFactory _debuggerProcessFactory;
    private CancellationTokenSource? _runCts;
    private IDapSession? _currentSession;
    private Stream? _currentInputStream;

    private IReadOnlyList<IVariableInfo> _lastDebugVariables = [];

    /// <summary>This host's own front-matter-derived options — available to <see cref="ApplyKindTransform"/>/<see cref="CalcKindOffset"/> overrides.</summary>
    protected CompiledDotnetLanguageHostOptions Options => _options;

    /// <summary>This language's project-file/import-syntax conventions — see <see cref="DotnetLanguageProjectProfile"/>.</summary>
    protected abstract DotnetLanguageProjectProfile ProjectProfile { get; }

    /// <summary>
    /// Rewrites the user's code per <see cref="RpadKind"/> before compiling — public (not just
    /// protected) so it stays directly unit-testable against a concrete host the way it always was,
    /// without requiring a test-only subclass. Must match <see cref="CalcKindOffset"/> exactly: the
    /// prefix this inserts is what DapSession maps a user-visible breakpoint line onto in the
    /// compiled source.
    /// </summary>
    public abstract string ApplyKindTransform(string code);

    /// <summary>
    /// Lines prepended before the user's own first line — both by <see cref="ApplyKindTransform"/>
    /// and by the compiler's own per-script global-usings/imports prepend (<see cref="Options"/>.Usings.Count).
    /// </summary>
    public abstract int CalcKindOffset();

    protected CompiledDotnetLanguageHostBase(
        CompiledDotnetLanguageHostOptions options,
        ILoggerFactory loggerFactory,
        DotnetProcessCompilerFactory? compilerFactory = null,
        ExecutionChildProcessFactory? executionProcessFactory = null,
        DebuggerChildProcessFactory? debuggerProcessFactory = null)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _compilerFactory = compilerFactory ?? CreateDefaultCompiler;
        _executionProcessFactory = executionProcessFactory ?? ChildProcess.ForExecution;
        _debuggerProcessFactory = debuggerProcessFactory ?? ChildProcess.ForDebugger;
        Directory.CreateDirectory(options.OutputDirectory);
    }

    private static IDotnetProcessCompiler CreateDefaultCompiler(
        DotnetLanguageProjectProfile profile, CompiledDotnetLanguageHostOptions options, ILoggerFactory loggerFactory) =>
        new MsBuildProcessCompiler(
            profile,
            sdk: options.Platform,
            executionOptions: options.ExecutionOptions,
            outputDir: options.OutputDirectory,
            runPadRuntimeLib: options.RuntimeLibPath,
            outputName: options.OutputName,
            nugetPackages: options.Packages,
            usings: options.Usings,
            assemblyReferences: options.AssemblyReferences,
            projectReferences: options.ProjectReferences,
            logger: loggerFactory.CreateLogger<MsBuildProcessCompiler>());

    // ── Compile ──────────────────────────────────────────────────────────────

    public override async Task<ICompilationResult> CompileAsync(
        string code, CancellationToken cancellationToken = default)
    {
        (CompiledDotnetCompilationResult result, _) = await CompileInternalAsync(ApplyKindTransform(code), cancellationToken);
        return result;
    }

    private async Task<(CompiledDotnetCompilationResult result, string? binaryPath)> CompileInternalAsync(
        string code, CancellationToken ct)
    {
        using var compiler = _compilerFactory(ProjectProfile, _options, _loggerFactory);

        ProcessCompilerResult raw = await compiler.Compilation(code, ct);
        CompiledDotnetCompilationResult result = new(raw);
        if (raw.Success)
            OnCompiled(raw.OutputBinaryFile);
        return (result, raw.Success ? raw.OutputBinaryFile : null);
    }

    // ── Run ──────────────────────────────────────────────────────────────────

    public override async Task RunAsync(
        string code,
        OutputReceivedHandler? outputHandler = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        (CompiledDotnetCompilationResult compilationResult, string? binaryPath) =
            await CompileInternalAsync(ApplyKindTransform(code), cancellationToken);
        EmitDiagnostics(compilationResult.Diagnostics, outputHandler);
        if (!compilationResult.Success || binaryPath is null) return;

        // No `using` here (ANALYSIS.md #99, same pattern as #89's CTS Cancel+Dispose fix) —
        // TerminateAsync can call _runCts.Cancel() from another thread (a "Stop" click racing this
        // method's own natural completion); disposing here would let that call land on an
        // already-disposed CTS and throw ObjectDisposedException. A CTS with no timer holds no
        // resources, so simply never disposing it is safe.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = cts;

        try
        {
            await using var process = _executionProcessFactory(
                _options.ExecutionOptions,
                binaryPath,
                Environment.ProcessId.ToString(),
                _options.CurrentQueryPath);

            if (!process.Start()) return;

            _currentInputStream = process.InputStream;
            await Task.WhenAll(
                DrainReaderAsync(process.StandardOutput, OutputKind.StandardOutput,
                    WrapWithSentinelDetection(outputHandler), cts.Token),
                DrainReaderAsync(process.StandardError, OutputKind.StandardError, outputHandler, cts.Token));
        }
        finally
        {
            _runCts = null;
            _currentInputStream = null;
        }
    }

    // ── Debug ─────────────────────────────────────────────────────────────────

    public override async Task RunDebugAsync(
        string code,
        OutputReceivedHandler outputHandler,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var lineOffset = CalcKindOffset();

        (CompiledDotnetCompilationResult compilationResult, string? binaryPath) =
            await CompileInternalAsync(ApplyKindTransform(code), cancellationToken);
        EmitDiagnostics(compilationResult.Diagnostics, outputHandler);
        if (!compilationResult.Success || binaryPath is null) return;

        // See RunAsync's own linked CTS above for why this isn't a `using` too.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = cts;

        try
        {
            await using var process = _debuggerProcessFactory(_options.ExecutionOptions);
            var sessionOptions = new DapSessionOptions(
                ProgramPath: binaryPath,
                WorkingDirectory: _options.OutputDirectory,
                Breakpoints: ActiveBreakpoints,
                SourceFileExtension: ProjectProfile.SourceFileExtension,
                LineOffset: lineOffset,
                BreakOnAllExceptions: _options.BreakOnAllExceptions,
                BreakOnUncaughtExceptions: _options.BreakOnUncaughtExceptions,
                CurrentQueryPath: _options.CurrentQueryPath,
                RuntimePath: _options.ExecutionOptions.Runtime.RuntimePath);
            await using var session = new DapSession(
                process,
                sessionOptions,
                WrapWithSentinelDetection(outputHandler),
                HandleBreakpointHit,
                _loggerFactory.CreateLogger<DapSession>(),
                OnBreakpointVerified);

            _currentSession = session;
            try
            {
                await session.RunAsync(cts.Token);
            }
            finally
            {
                session.FlushOutputBuffers();
                _currentSession = null;
            }
        }
        finally
        {
            _runCts = null;
        }
    }

    public override async Task PauseAsync(CancellationToken cancellationToken)
    {
        EnsureCapability(LanguageHostCapabilities.Debug);
        if (_currentSession is { } s)
            await s.PauseAsync(cancellationToken);
    }

    // Forward gutter breakpoint edits made during an active debug session to the DAP session, so
    // netcoredbg's own breakpoint set doesn't go stale relative to what's shown in the editor.
    // The resend itself only happens the next time execution naturally stops (see
    // DapSession.RequestBreakpointResync) — sending "setBreakpoints" while the debuggee is
    // running would race the session's single message-read loop.
    public override void AddBreakpoint(IBreakpoint breakpoint)
    {
        base.AddBreakpoint(breakpoint);
        _currentSession?.RequestBreakpointResync();
    }

    public override void RemoveBreakpoint(IBreakpoint breakpoint)
    {
        base.RemoveBreakpoint(breakpoint);
        _currentSession?.RequestBreakpointResync();
    }

    private void HandleBreakpointHit(IBreakpointHitContext context)
    {
        _lastDebugVariables = context.LocalVariables;
        OnBreakpointHit(context);
    }

    // ── Terminate ─────────────────────────────────────────────────────────────

    public override Task TerminateAsync(CancellationToken cancellationToken)
    {
        _runCts?.Cancel();
        return Task.CompletedTask;
    }

    // ── Memory dump ───────────────────────────────────────────────────────────

    public override Task<IMemoryDump> GetMemoryDumpAsync(CancellationToken cancellationToken)
    {
        EnsureCapability(LanguageHostCapabilities.MemoryDump);

        IMemoryDump dump = new DapMemoryDump
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Variables = _lastDebugVariables,
        };
        return Task.FromResult(dump);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public override async ValueTask DisposeAsync()
    {
        // RunAsync/RunDebugAsync never dispose _runCts themselves (see the comment where it's
        // created), so there's nothing here that can throw ObjectDisposedException anymore.
        if (_runCts is not null)
            await _runCts.CancelAsync();

        await base.DisposeAsync();
    }

    // ── Interactive input ────────────────────────────────────────────────────

    // No-op if nothing is running via RunAsync — RunDebugAsync never sets _currentInputStream
    // (see WrapWithSentinelDetection), so this can never race a real send with a torn-down process.
    public override async Task SendInputAsync(string text, CancellationToken cancellationToken)
    {
        if (_currentInputStream is not { } stream) return;
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Recognizes both sentinel-prefixed lines a running script can emit on stdout: RsPrefix for
    // ObjectExtensions.Dump (DumpHtml, unconditional) and InputRequestPrefix for Util.ReadLine/
    // GetPassword (only while _currentInputStream is set, i.e. a real RunAsync process — RunDebugAsync
    // shares this same wrapper but never sets that field, so a script calling Util.ReadLine under the
    // debugger falls through to the plain-output branch instead of firing an unanswerable request).
    private OutputReceivedHandler? WrapWithSentinelDetection(OutputReceivedHandler? handler)
    {
        if (handler is null) return null;
        return (data, kind) =>
        {
            if (data.Length > 0 && data[0] == RsPrefix)
            {
                handler(data[1..], OutputKind.DumpHtml);
            }
            else if (data.Length > 0 && data[0] == InputRequestPrefix && _currentInputStream is not null)
            {
                (string? prompt, bool isPassword) = ParseInputRequest(data[1..]);
                OnInputRequested(prompt, isPassword);
            }
            else
            {
                handler(data, kind);
            }
        };
    }

    private static (string? Prompt, bool IsPassword) ParseInputRequest(string base64)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var prompt = root.TryGetProperty("Prompt", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
            var isPassword = root.TryGetProperty("IsPassword", out var ip) && ip.ValueKind == JsonValueKind.True;
            return (prompt, isPassword);
        }
        catch (Exception)
        {
            return (null, false);
        }
    }

    private static void EmitDiagnostics(IReadOnlyList<IDiagnostic> diagnostics, OutputReceivedHandler? handler)
    {
        if (handler is null) return;
        foreach (IDiagnostic d in diagnostics)
        {
            string severity = d.Severity.ToString().ToLowerInvariant();
            // Line 0 = no source position (e.g. "CSC : error CS5001", MSBuild-level errors) — a
            // "(0,0)" prefix would point at a location that doesn't exist.
            string position = d.Line > 0 ? $"({d.Line},{d.Column}): " : "";
            string code = d.Code.Length > 0 ? $" {d.Code}" : "";
            string text = $"{position}{severity}{code}: {d.Message}";
            OutputKind kind = d.Severity == DiagnosticSeverity.Error
                ? OutputKind.StandardError
                : OutputKind.StandardOutput;
            handler(text, kind);
        }
    }

    private static async Task DrainReaderAsync(
        StreamReader reader,
        OutputKind kind,
        OutputReceivedHandler? handler,
        CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
            handler?.Invoke(line, kind);
    }
}
