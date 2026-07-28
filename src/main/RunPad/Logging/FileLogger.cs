using Microsoft.Extensions.Logging;

namespace RunPad.Logging;

internal sealed class FileLogger(string outDir, string category) : ILogger
{
    private static readonly object _lock = new();

    // Keep the active day-file bounded; one ".1" backup is kept alongside it, so worst case is
    // roughly 2x this per day rather than an unbounded, ever-growing file.
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    // Trim namespace prefix: "RunPad.Debug.DapSession" → "DapSession"
    private readonly string _shortCategory = category.Contains('.')
        ? category[(category.LastIndexOf('.') + 1)..]
        : category;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // ToDo: Вынести в инициализацию
        var filePrefix = "runpad";
        if (!IsEnabled(logLevel)) return;
        var now = DateTime.Now;
        var file = Path.Combine(outDir, $"{filePrefix}-{DateTime.Now:yyyy-MM-dd}.log");

        var message = formatter(state, exception);
        var timestamp = now.ToString("yyyy-MM-dd HH:mm:ss");

        var level = logLevel switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };

        var logLine = $"[{timestamp}] [{level}] [{_shortCategory}] {message}";

        if (exception is not null)
        {
            logLine += Environment.NewLine + exception;
        }

        lock (_lock)
        {
            RotateIfTooLarge(file);
            File.AppendAllText(file, logLine + Environment.NewLine);
        }
    }

    private static void RotateIfTooLarge(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length < MaxFileSizeBytes) return;

        var rotated = Path.Combine(
            Path.GetDirectoryName(file)!,
            $"{Path.GetFileNameWithoutExtension(file)}.1{Path.GetExtension(file)}");

        File.Delete(rotated);
        File.Move(file, rotated);
    }
}
