using System.Threading.Channels;

namespace DotPad.Build.Core.ProcessRunners;

public record ReaderMessage(string Data);
public record WriterMessage(string Data);

public interface IExecutionProcess : IAsyncDisposable
{
    ChannelReader<string> Reader { get; }
    ChannelReader<string> ErrorReader { get; }
    ChannelWriter<string> InputWriter { get; }

    Task Start(CancellationToken ct);
    Task<bool> Close(CancellationToken ct);
    Task<int> WaitForExit(CancellationToken ct);
}
