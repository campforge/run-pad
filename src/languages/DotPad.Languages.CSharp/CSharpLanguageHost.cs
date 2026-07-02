using System.Text;
using System.Threading.Channels;
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
using OutputKind = DotPad.Contracts.Models.OutputKind;

namespace DotPad.Languages.CSharp;

public sealed class CSharpLanguageHost : LanguageHostBase
{
    private readonly CSharpLanguageHostOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private CancellationTokenSource? _runCts;
    private CSharpDapSession? _currentSession;

    private IReadOnlyList<IVariableInfo> _lastDebugVariables = [];

    public override string Language => "csharp";

    public override LanguageHostCapabilities Capabilities =>
        LanguageHostCapabilities.Run               |
        LanguageHostCapabilities.Debug             |
        LanguageHostCapabilities.Breakpoints       |
        LanguageHostCapabilities.VariableInspection|
        LanguageHostCapabilities.MemoryDump        |
        LanguageHostCapabilities.IntelliSense;

    public override IIntelliSenseService CreateIntelliSenseService()
    {
        var refs = new CSharpReferenceService();
        var paths = refs.GetDefault().Append(_options.RuntimeLibPath).ToList();
        return new CSharpIntelliSenseService(paths, _loggerFactory.CreateLogger<CSharpIntelliSenseService>());
    }

    internal CSharpLanguageHost(CSharpLanguageHostOptions options, ILoggerFactory loggerFactory)
    {
        _options       = options;
        _loggerFactory = loggerFactory;
        Directory.CreateDirectory(options.OutputDirectory);
    }

    // ── Compile ──────────────────────────────────────────────────────────────

    public override async Task<ICompilationResult> CompileAsync(
        string code, CancellationToken cancellationToken = default)
    {
        var (result, _) = await CompileInternalAsync(code, cancellationToken);
        return result;
    }

    private async Task<(CSharpCompilationResult result, string? binaryPath)> CompileInternalAsync(
        string code, CancellationToken ct)
    {
        var syntaxTree = BuildSyntaxTree(code);

        using var compiler = new DotnetProcessCompiler(
            _options.Platform,
            _options.ExecutionOptions,
            _options.OutputDirectory,
            _options.RuntimeLibPath,
            _options.OutputName,
            _loggerFactory.CreateLogger<DotnetProcessCompiler>());

        var raw    = await compiler.Compilation(syntaxTree, ct);
        var result = new CSharpCompilationResult(raw);
        return (result, raw.Success ? raw.OutputBinaryFile : null);
    }

    private SyntaxTree BuildSyntaxTree(string code)
    {
        var bytes = Encoding.UTF8.GetBytes(code);
        var text  = SourceText.From(bytes, bytes.Length, Encoding.UTF8, canBeEmbedded: true);
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

        var (compilationResult, binaryPath) = await CompileInternalAsync(code, cancellationToken);
        if (!compilationResult.Success || binaryPath is null) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = cts;

        try
        {
            await using var process = new LocalProcess(
                _options.ExecutionOptions,
                new LocalExecutionArgs(binaryPath, Environment.ProcessId.ToString()));

            var ioTask = process.Start(cts.Token);

            await Task.WhenAll(
                DrainChannelAsync(process.Reader,      OutputKind.StandardOutput, WrapWithDumpDetection(outputHandler), cts.Token),
                DrainChannelAsync(process.ErrorReader, OutputKind.StandardError,  outputHandler, cts.Token),
                ioTask);

            await process.WaitForExit(cts.Token);
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

        var (compilationResult, binaryPath) = await CompileInternalAsync(code, cancellationToken);
        if (!compilationResult.Success || binaryPath is null) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = cts;

        try
        {
            await using var process = new LocalDebuggerProcess(
                _options.ExecutionOptions,
                new LocalDebuggerExecutionArgs());

            await using var session = new CSharpDapSession(
                process,
                binaryPath,
                _options.OutputDirectory,
                ActiveBreakpoints,
                WrapWithDumpDetection(outputHandler),
                HandleBreakpointHit,
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
            Variables  = _lastDebugVariables,
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

    private const char   RsPrefix   = '\x1e';        // new format: RS + base64
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

    private static async Task DrainChannelAsync(
        ChannelReader<string> reader,
        OutputKind kind,
        OutputReceivedHandler? handler,
        CancellationToken ct)
    {
        while (await reader.WaitToReadAsync(ct))
        {
            var line = await reader.ReadAsync(ct);
            handler?.Invoke(line, kind);
        }
    }
}
