using Microsoft.Extensions.Logging;

namespace DotPad.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLoggerProvider(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _writer = new StreamWriter(filePath, append: true, System.Text.Encoding.UTF8) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _lock);

    public void Dispose() => _writer.Dispose();
}

internal sealed class FileLogger(string category, StreamWriter writer, object writeLock) : ILogger
{
    // Trim namespace prefix: "DotPad.Languages.CSharp.Debug.CSharpDapSession" → "CSharpDapSession"
    private readonly string _shortCategory = category.Contains('.')
        ? category[(category.LastIndexOf('.') + 1)..]
        : category;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var level = logLevel switch
        {
            LogLevel.Trace       => "TRC",
            LogLevel.Debug       => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning     => "WRN",
            LogLevel.Error       => "ERR",
            LogLevel.Critical    => "CRT",
            _                    => "???"
        };

        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {_shortCategory}: {formatter(state, exception)}";
        if (exception is not null)
            text += Environment.NewLine + exception;

        lock (writeLock)
            writer.WriteLine(text);
    }
}
