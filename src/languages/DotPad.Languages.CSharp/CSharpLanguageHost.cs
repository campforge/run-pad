using DotPad.Build.Core.ProcessRunners;
using DotPad.Contracts;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp.Adapters;
using DotPad.Languages.CSharp.Compiler;
using DotPad.Languages.CSharp.Debug;
using Microsoft.Extensions.Logging;
using DiagnosticSeverity = DotPad.Contracts.Models.DiagnosticSeverity;
using OutputKind = DotPad.Contracts.Models.OutputKind;

namespace DotPad.Languages.CSharp;

public sealed class CSharpLanguageHost : LanguageHostBase
{
    private readonly CSharpLanguageHostOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private CancellationTokenSource? _runCts;
    private IDapSession? _currentSession;

    private IReadOnlyList<IVariableInfo> _lastDebugVariables = [];

    public override string Language => "csharp";

    public override LanguageHostCapabilities Capabilities =>
        LanguageHostCapabilities.Run |
        LanguageHostCapabilities.Debug |
        LanguageHostCapabilities.Breakpoints |
        LanguageHostCapabilities.VariableInspection |
        LanguageHostCapabilities.MemoryDump |
        LanguageHostCapabilities.IntelliSense;

    internal CSharpLanguageHost(CSharpLanguageHostOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        Directory.CreateDirectory(options.OutputDirectory);
    }

    // ── Compile ──────────────────────────────────────────────────────────────

    public override async Task<ICompilationResult> CompileAsync(
        string code, CancellationToken cancellationToken = default)
    {
        (CSharpCompilationResult result, _) = await CompileInternalAsync(ApplyKindTransform(code), cancellationToken);
        return result;
    }

    private async Task<(CSharpCompilationResult result, string? binaryPath)> CompileInternalAsync(
        string code, CancellationToken ct)
    {
        // Fast path: no NuGet packages and no project references means nothing needs MSBuild's own
        // restore/reference-resolution — compile in-process and skip the dotnet-build subprocess
        // entirely (see ANALYSIS.md feature #16). Anything else falls back to the process compiler,
        // which already handles the full range of packages/references/target frameworks.
        using ProcessCompilerBase compiler = new DotnetProcessCompiler(
            sdk: _options.Platform,
            executionOptions: _options.ExecutionOptions,
            outputDir: _options.OutputDirectory,
            dotPadRuntimeLib: _options.RuntimeLibPath,
            outputName: _options.OutputName,
            nugetPackages: _options.Packages,
            usings: _options.Usings,
            assemblyReferences: _options.AssemblyReferences,
            projectReferences: _options.ProjectReferences,
            logger: _loggerFactory.CreateLogger<DotnetProcessCompiler>());

        ProcessCompilerResult raw = await compiler.Compilation(code, ct);
        CSharpCompilationResult result = new(raw);
        return (result, raw.Success ? raw.OutputBinaryFile : null);
    }

    // ── Run ──────────────────────────────────────────────────────────────────

    public override async Task RunAsync(
        string code,
        OutputReceivedHandler? outputHandler = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        (CSharpCompilationResult compilationResult, string? binaryPath) =
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

        (CSharpCompilationResult compilationResult, string? binaryPath) =
            await CompileInternalAsync(ApplyKindTransform(code), cancellationToken);
        EmitDiagnostics(compilationResult.Diagnostics, outputHandler);
        if (!compilationResult.Success || binaryPath is null) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = cts;

        try
        {
            await using var process = ChildProcess.ForDebugger(_options.ExecutionOptions);
            await using var session = new CSharpDapSession(
                process,
                binaryPath,
                _options.OutputDirectory,
                ActiveBreakpoints,
                WrapWithDumpDetection(outputHandler),
                HandleBreakpointHit,
                lineOffset,
                _loggerFactory.CreateLogger<CSharpDapSession>(),
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

    // Forward gutter breakpoint edits made during an active debug session to the DAP session, so
    // netcoredbg's own breakpoint set doesn't go stale relative to what's shown in the editor.
    // The resend itself only happens the next time execution naturally stops (see
    // CSharpDapSession.RequestBreakpointResync) — sending "setBreakpoints" while the debuggee is
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

    private string ApplyKindTransform(string code) => _options.Kind switch
    {
        RpadKind.Expression => $"(\n{code.TrimEnd()}\n).Dump();\n",
        RpadKind.Program => $"Main();{code}",
        _ => code,
    };

    private int CalcKindOffset() => _options.Kind switch
    {
        RpadKind.Expression => 1,
        RpadKind.Program => 2,
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
