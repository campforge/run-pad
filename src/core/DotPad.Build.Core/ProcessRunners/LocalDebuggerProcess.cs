using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using DotPad.Build.Core.Models;

namespace DotPad.Build.Core.ProcessRunners;

public class LocalDebuggerExecutionArgs
{
    public override string ToString() => " --interpreter=vscode";
}

/// <summary>
/// Wraps the netcoredbg process and exposes raw byte streams for DAP framing.
/// stdout/stdin are raw Streams; stderr is drained into a channel for diagnostics.
/// </summary>
public sealed class LocalDebuggerProcess : IAsyncDisposable
{
    private readonly Channel<string> _errorChannel = Channel.CreateUnbounded<string>();
    private readonly Process _process;
    private Task? _stderrTask;

    /// <summary>Raw stdout stream — read DAP messages here (Content-Length framing).</summary>
    public Stream OutputStream { get; private set; } = Stream.Null;

    /// <summary>Raw stdin stream — write DAP messages here (Content-Length framing).</summary>
    public Stream InputStream { get; private set; } = Stream.Null;

    /// <summary>Stderr lines from netcoredbg (diagnostic output).</summary>
    public ChannelReader<string> ErrorReader => _errorChannel.Reader;

    public LocalDebuggerProcess(ExecutionOptions options, LocalDebuggerExecutionArgs args)
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = options.Debugger.DebuggerPath,
                Arguments              = args.ToString(),
                WorkingDirectory       = options.WorkingDirectory,
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = true,
                StandardErrorEncoding  = Encoding.UTF8,
            }
        };
    }

    public void Start(CancellationToken ct)
    {
        _process.Start();
        // Access BaseStream directly — bypasses StreamReader/Writer buffering and encoding.
        OutputStream = _process.StandardOutput.BaseStream;
        InputStream  = _process.StandardInput.BaseStream;
        _stderrTask  = PumpStderrAsync(ct);
    }

    private async Task PumpStderrAsync(CancellationToken ct)
    {
        var reader = _process.StandardError;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                _errorChannel.Writer.TryWrite(line);
            }
        }
        catch (OperationCanceledException) { }
        finally { _errorChannel.Writer.TryComplete(); }
    }

    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        try { await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { }
        if (_stderrTask is not null)
            try { await _stderrTask.ConfigureAwait(false); }
            catch { }
        _process.Dispose();
    }
}
