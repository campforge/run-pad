using Microsoft.Extensions.Logging;

namespace RunPad.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _outDir;

    public FileLoggerProvider(string outDir)
    {
        _outDir = outDir;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_outDir, categoryName);

    public void Dispose()
    {
    }
}
