using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Invoklet.Services.Preferences;

/// <summary>How much of what Invoklet itself does is written to its log files and console (Preferences → Logging).</summary>
public enum LogVerbosity
{
    /// <summary>Nothing is logged and no log file is made.</summary>
    Off,

    /// <summary>Only the failures Invoklet cannot go on after: an unhandled exception, or a start that failed.</summary>
    Fatal,

    /// <summary>Failures of what Invoklet was doing (an operation that threw), and the fatal ones.</summary>
    Error,

    /// <summary>Also what went wrong without stopping anything.</summary>
    Warning,

    /// <summary>Also the ordinary events: the default.</summary>
    Information,

    /// <summary>Also the details, including debugger traffic and snippets of the user's script code.</summary>
    Debug,
}

public static class LogVerbosityExtensions
{
    /// <summary>The least severe level that is still written, or <see cref="LogLevel.None"/> when nothing is.</summary>
    public static LogLevel MinimumLevel(this LogVerbosity verbosity) => verbosity switch
    {
        LogVerbosity.Off => LogLevel.None,
        LogVerbosity.Fatal => LogLevel.Critical,
        LogVerbosity.Error => LogLevel.Error,
        LogVerbosity.Warning => LogLevel.Warning,
        LogVerbosity.Information => LogLevel.Information,
        LogVerbosity.Debug => LogLevel.Debug,
        _ => LogLevel.Information,
    };
}

public sealed class LoggingSettings
{
    /// <summary>Stored by name in settings.json, so the file stays readable and a value added later does not shift the others.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LogVerbosity>))]
    public LogVerbosity Level { get; set; } = LogVerbosity.Information;
}
