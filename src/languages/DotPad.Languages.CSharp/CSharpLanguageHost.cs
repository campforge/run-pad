using System.Text;
using DotPad.Build.Core.ProcessRunners;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Contracts.Models;
using DotPad.Languages.CSharp.Adapters;
using DotPad.Languages.CSharp.Compiler;
using DotPad.Languages.CSharp.Debug;
using DotPad.Languages.CSharp.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
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

    // BUG-9: этот метод никогда не вызывается — DI регистрирует IIntelliSenseService напрямую в App.axaml.cs.
    // Следствие: RuntimeLibPath не попадает в Roslyn → .Dump() отсутствует в автодополнении.
    public override IIntelliSenseService CreateIntelliSenseService()
    {
        var refs = new CSharpReferenceService();
        var paths = refs.GetDefault().Append(_options.RuntimeLibPath).ToList();
        return new CSharpIntelliSenseService(paths, _loggerFactory.CreateLogger<CSharpIntelliSenseService>());
    }

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
        SyntaxTree syntaxTree = BuildSyntaxTree(code);

        using DotnetProcessCompiler compiler = new(
            _options.Platform,
            _options.ExecutionOptions,
            _options.OutputDirectory,
            _options.RuntimeLibPath,
            _options.OutputName,
            _options.NugetPackages,
            _options.GlobalUsings,
            _options.AssemblyReferences,
            _options.ProjectReferences,
            _options.TargetFrameworkOverride,
            _loggerFactory.CreateLogger<DotnetProcessCompiler>());

        ProcessCompilerResult raw = await compiler.Compilation(syntaxTree, ct);
        CSharpCompilationResult result = new(raw);
        return (result, raw.Success ? raw.OutputBinaryFile : null);
    }

    private SyntaxTree BuildSyntaxTree(string code)
    {
        var bytes = Encoding.UTF8.GetBytes(code);
        var text = SourceText.From(bytes, bytes.Length, Encoding.UTF8, canBeEmbedded: true);
        return CSharpSyntaxTree.ParseText(
            text,
            new CSharpParseOptions(),
            Path.Combine(_options.OutputDirectory, $"{_options.OutputName}.cs"));
    }

    // ── Run ──────────────────────────────────────────────────────────────────

    public override async Task RunAsync(
        string code,
        OutputReceivedHandler? outputHandler = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        (CSharpCompilationResult compilationResult, string? binaryPath) = await CompileInternalAsync(ApplyKindTransform(code), cancellationToken);
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
                DrainReaderAsync(process.StandardOutput, OutputKind.StandardOutput, WrapWithDumpDetection(outputHandler), cts.Token),
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
        OutputReceivedHandler? outputHandler = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        (CSharpCompilationResult compilationResult, string? binaryPath) = await CompileInternalAsync(ApplyKindTransform(code), cancellationToken);
        EmitDiagnostics(compilationResult.Diagnostics, outputHandler);
        if (!compilationResult.Success || binaryPath is null) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = cts;

        try
        {
            await using var process = ChildProcess.ForDebugger(_options.ExecutionOptions);

            int lineOffset = _options.GlobalUsings.Count
                + (_options.Kind is "Program" or "Expression" ? 1 : 0);

            await using var session = new CSharpDapSession(
                process,
                binaryPath,
                _options.OutputDirectory,
                ActiveBreakpoints,
                WrapWithDumpDetection(outputHandler),
                HandleBreakpointHit,
                lineOffset,
                _loggerFactory.CreateLogger<CSharpDapSession>());

            _currentSession = session;
            try { await session.RunAsync(cts.Token); }
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

    public override async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        EnsureCapability(LanguageHostCapabilities.Debug);
        if (_currentSession is { } s)
            await s.PauseAsync(cancellationToken);
    }

    private void HandleBreakpointHit(IBreakpointHitContext context)
    {
        _lastDebugVariables = context.LocalVariables;
        OnBreakpointHit(context);
    }

    // ── Terminate ─────────────────────────────────────────────────────────────

    public override Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        _runCts?.Cancel();
        return Task.CompletedTask;
    }

    // ── Memory dump ───────────────────────────────────────────────────────────

    public override Task<IMemoryDump> GetMemoryDumpAsync(CancellationToken cancellationToken = default)
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
        _runCts?.Cancel();
        await base.DisposeAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Transforms user code based on the script kind before compilation.
    // Program: prepend Main() call (user declares void Main(), runtime calls it).
    // Expression: wrap expression in parentheses and call .Dump() on the result.
    // Statements: pass through unchanged (top-level statements, already the default).
    private string ApplyKindTransform(string code) => _options.Kind switch
    {
        "Program"    => $"Main();\n{code}",
        "Expression" => $"(\n{code.TrimEnd()}\n).Dump();\n",
        _            => code,
    };

    private const char RsPrefix = '\x1e';        // new format: RS + base64
    private const string TextPrefix = "DOTPAD_DUMP:"; // legacy format

    private static OutputReceivedHandler? WrapWithDumpDetection(OutputReceivedHandler? handler)
    {
        if (handler is null) return null;
        return (data, kind) =>
        {
            if (data.Length > 0 && data[0] == RsPrefix)
                handler(data[1..], OutputKind.DumpHtml);
            else if (data.StartsWith(TextPrefix, StringComparison.Ordinal))
                handler(data[TextPrefix.Length..], OutputKind.DumpHtml);
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
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
            handler?.Invoke(line, kind);
    }
}
