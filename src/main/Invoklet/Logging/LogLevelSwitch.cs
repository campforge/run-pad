using Invoklet.Services.Preferences;
using Microsoft.Extensions.Logging;

namespace Invoklet.Logging;

/// <summary>
/// The one setting every logger of the application asks before it writes, so the level can be changed while Invoklet runs
/// (Preferences → Logging) without building the loggers again.
/// </summary>
public sealed class LogLevelSwitch(LogVerbosity initial = LogVerbosity.Information)
{
    private volatile LogVerbosity _verbosity = initial;

    public LogVerbosity Verbosity => _verbosity;

    public void Apply(LogVerbosity verbosity) => _verbosity = verbosity;

    /// <summary>Whether something logged at <paramref name="level"/> is written now.</summary>
    public bool IsEnabled(LogLevel level)
    {
        var minimum = _verbosity.MinimumLevel();
        return minimum != LogLevel.None && level != LogLevel.None && level >= minimum;
    }
}
