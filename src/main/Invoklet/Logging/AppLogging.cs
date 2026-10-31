using Microsoft.Extensions.Logging;

namespace Invoklet.Logging;

public static class AppLogging
{
    /// <summary>
    /// Sends the logs to the console and to <paramref name="fileProvider"/>, as far as <paramref name="levelSwitch"/> allows. The
    /// loggers themselves accept every level and the switch decides at each call, which is what lets the setting change while
    /// Invoklet runs.
    /// </summary>
    public static void Configure(ILoggingBuilder builder, LogLevelSwitch levelSwitch, ILoggerProvider fileProvider)
    {
        builder.AddConsole();
        builder.AddProvider(fileProvider);
        builder.SetMinimumLevel(LogLevel.Trace);
        builder.AddFilter((LogLevel level) => levelSwitch.IsEnabled(level));
    }
}
