using Invoklet.Logging;
using Invoklet.Services.Preferences;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Invoklet.NonUI.Tests.App.Logging.Fixtures;

// The logging of the application as it is configured, with a provider that keeps what it is given in place of the files.
internal sealed class AppLoggingFixture : IDisposable
{
    private readonly ServiceProvider _provider;

    public RecordingProvider Recorder { get; } = new();

    public LogLevelSwitch Switch { get; }

    public AppLoggingFixture(LogVerbosity verbosity)
    {
        Switch = new LogLevelSwitch(verbosity);
        _provider = new ServiceCollection().AddLogging(lb => AppLogging.Configure(lb, Switch, Recorder)).BuildServiceProvider();
    }

    public ILogger Logger => _provider.GetRequiredService<ILoggerFactory>().CreateLogger("Test");

    public void Dispose() => _provider.Dispose();

    internal sealed class RecordingProvider : ILoggerProvider
    {
        public List<LogLevel> Levels { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Levels);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(List<LogLevel> levels) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => levels.Add(logLevel);
    }
}
