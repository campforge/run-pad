using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using DotPad.Build.Core.Models;

namespace DotPad.Build.Core.ProcessRunners;

public class LocalProcess : IExecutionProcess, IAsyncDisposable
{
    private readonly Channel<string> _outputChannel = Channel.CreateBounded<string>(5);
    private readonly Channel<string> _inputChannel  = Channel.CreateBounded<string>(5);
    private readonly Channel<string> _errorOutputChannel = Channel.CreateBounded<string>(5);

    public ChannelReader<string> Reader      => _outputChannel.Reader;
    public ChannelReader<string> ErrorReader => _errorOutputChannel.Reader;
    public ChannelWriter<string> InputWriter => _inputChannel.Writer;

    private readonly Process _process;

    public LocalProcess(ExecutionOptions options, LocalExecutionArgs args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName               = options.Runtime.RuntimePath,
            Arguments              = args.ToString(),
            WorkingDirectory       = options.WorkingDirectory,
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        _process = new Process();
        _process.StartInfo = startInfo;
    }

    public async Task Start(CancellationToken ct)
    {
        if (!_process.Start())
            return;
        await Task.WhenAll(
            ReadOutput(_process.StandardOutput, ct),
            ReadErrors(_process.StandardError,  ct));
    }

    private async Task ReadErrors(StreamReader reader, CancellationToken cancellationToken)
    {
        while (await _errorOutputChannel.Writer.WaitToWriteAsync(cancellationToken))
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;
            await _errorOutputChannel.Writer.WriteAsync(line, cancellationToken);
        }
        _errorOutputChannel.Writer.Complete();
    }

    private async Task ReadOutput(StreamReader reader, CancellationToken cancellationToken)
    {
        while (await _outputChannel.Writer.WaitToWriteAsync(cancellationToken))
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;
            await _outputChannel.Writer.WriteAsync(line, cancellationToken);
        }
        _outputChannel.Writer.Complete();
    }

    public async Task<bool> Close(CancellationToken ct)
    {
        try { _process.Kill(true); }
        catch (Exception) { return false; }

        await _process.WaitForExitAsync(ct);
        return true;
    }

    public async Task<int> WaitForExit(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct);
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is IAsyncDisposable processAsyncDisposable)
            await processAsyncDisposable.DisposeAsync();
        else
            _process.Dispose();
    }
}
