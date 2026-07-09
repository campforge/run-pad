using DotPad.Build.Core.ProcessRunners;
using DotPad.Contracts;
using DotPad.Contracts.Models;
using DotPad.Debug;
using DotPad.Format.RPad;
using DotPad.Languages.VB.Adapters;
using DotPad.Languages.VB.Compiler;
using Microsoft.Extensions.Logging;
using DiagnosticSeverity = DotPad.Contracts.Models.DiagnosticSeverity;
using OutputKind = DotPad.Contracts.Models.OutputKind;

namespace DotPad.Languages.VB;

public sealed class VbLanguageHost : LanguageHostBase
{
    private readonly VbLanguageHostOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private CancellationTokenSource? _runCts;
    private IDapSession? _currentSession;

    private IReadOnlyList<IVariableInfo> _lastDebugVariables = [];

    public override string Language => "vb";

    public override LanguageHostCapabilities Capabilities =>
        LanguageHostCapabilities.Run |
        LanguageHostCapabilities.Debug |
        LanguageHostCapabilities.Breakpoints |
        LanguageHostCapabilities.VariableInspection |
        LanguageHostCapabilities.MemoryDump;

    internal VbLanguageHost(VbLanguageHostOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        Directory.CreateDirectory(options.OutputDirectory);
    }

    // ── Compile ──────────────────────────────────────────────────────────────

    public override async Task<ICompilationResult> CompileAsync(
        string code, CancellationToken cancellationToken = default)
    {
        (VbCompilationResult result, _) = await CompileInternalAsync(ApplyKindTransform(code), cancellationToken);
        return result;
    }

    private async Task<(VbCompilationResult result, string? binaryPath)> CompileInternalAsync(
        string code, CancellationToken ct)
    {
        var compiler = new VbProcessCompiler(
            sdk: _options.Platform,
            executionOptions: _options.ExecutionOptions,
            outputDir: _options.OutputDirectory,
            dotPadRuntimeLib: _options.RuntimeLibPath,
            outputName: _options.OutputName,
            nugetPackages: _options.Packages,
            usings: _options.Usings,
            assemblyReferences: _options.AssemblyReferences,
            projectReferences: _options.ProjectReferences,
            logger: _loggerFactory.CreateLogger<VbProcessCompiler>());
        using var _1 = compiler;

        ProcessCompilerResult raw = await compiler.Compilation(code, ct);
        VbCompilationResult result = new(raw);
        return (result, raw.Success ? raw.OutputBinaryFile : null);
    }

    // ── Run ──────────────────────────────────────────────────────────────────

    public override async Task RunAsync(
        string code,
        OutputReceivedHandler? outputHandler = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        (VbCompilationResult compilationResult, string? binaryPath) =
            await CompileInternalAsync(ApplyKindTransform(code), cancellationToken);
        EmitDiagnostics(compilationResult.Diagnostics, outputHandler);
        if (!compilationResult.Success || binaryPath is null) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = cts;

        try
        {
            await using var process = ChildProcess.ForExecution(
                _options.ExecutionOptions,
                binaryPath,
                Environment.ProcessId.ToString());

            if (!process.Start()) return;

            await Task.WhenAll(
                DrainReaderAsync(process.StandardOutput, OutputKind.StandardOutput,
                    WrapWithDumpDetection(outputHandler), cts.Token),
                DrainReaderAsync(process.StandardError, OutputKind.StandardError, outputHandler, cts.Token));
        }
        finally
        {
            _runCts = null;
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
        code = ApplyKindTransform(code);

        (VbCompilationResult compilationResult, string? binaryPath) =
            await CompileInternalAsync(ApplyKindTransform(code), cancellationToken);
        EmitDiagnostics(compilationResult.Diagnostics, outputHandler);
        if (!compilationResult.Success || binaryPath is null) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = cts;

        try
        {
            await using var process = ChildProcess.ForDebugger(_options.ExecutionOptions);
            await using var session = new DapSession(
                process,
                binaryPath,
                _options.OutputDirectory,
                ActiveBreakpoints,
                ".vb",
                WrapWithDumpDetection(outputHandler),
                HandleBreakpointHit,
                lineOffset,
                _loggerFactory.CreateLogger<DapSession>(),
                _options.BreakOnAllExceptions,
                _options.BreakOnUncaughtExceptions,
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

    // See CSharpLanguageHost's identical override for why this resync is needed.
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
        try
        {
            if (_runCts is not null)
                await _runCts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }

        await base.DisposeAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const char RsPrefix = '\x1e'; // new format: RS + base64

    // VB has no top-level-statements feature (unlike C# 9+), so every runnable entry point needs an
    // explicit Module/Sub Main — the two kinds effectively swap roles versus CSharpLanguageHost:
    //   Statements → wrap in a generated Sub Main (user writes bare statements, no boilerplate).
    //   Program    → passthrough (user supplies their own full Module ... Sub Main ... End Module).
    //   Expression → wrap as (code).Dump() — no trailing semicolon, VB's statement separator is a
    //                newline, not ';'.
    private string ApplyKindTransform(string code) => _options.Kind switch
    {
        RpadKind.Statements => $"Module Program\nSub Main()\n{code}\nEnd Sub\nEnd Module\n",
        RpadKind.Expression => $"(\n{code.TrimEnd()}\n).Dump()\n",
        _ => code,
    };

    // Lines prepended before the user's first line by ApplyKindTransform, above — must match it
    // exactly so DapSession can map a breakpoint set on the user-visible line to the right line in
    // the actually-compiled (wrapped) source.
    private int CalcKindOffset() => _options.Kind switch
    {
        RpadKind.Statements => 2, // "Module Program" + "Sub Main()"
        RpadKind.Expression => 1, // "("
        _ => 0,
    };

    private static OutputReceivedHandler? WrapWithDumpDetection(OutputReceivedHandler? handler)
    {
        if (handler is null) return null;
        return (data, kind) =>
        {
            if (data.Length > 0 && data[0] == RsPrefix)
                handler(data[1..], OutputKind.DumpHtml);
            else
                handler(data, kind);
        };
    }

    private static void EmitDiagnostics(IReadOnlyList<IDiagnostic> diagnostics, OutputReceivedHandler? handler)
    {
        if (handler is null) return;
        foreach (IDiagnostic d in diagnostics)
        {
            string severity = d.Severity.ToString().ToLowerInvariant();
            string text = $"({d.Line},{d.Column}): {severity} {d.Code}: {d.Message}";
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
