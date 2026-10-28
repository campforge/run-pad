namespace RunPad.Desktop;

/// <summary>
/// An on/off setting an OS integration offers; Preferences shows it on Interface → System as a
/// check box with its title and description.
/// </summary>
public sealed class SystemSetting
{
    /// <summary>Key in RunPad's settings file, prefixed with the OS, such as "linux.nativeWayland".</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public bool DefaultValue { get; init; }

    /// <summary>Only read when RunPad starts, so a change waits for the next start.</summary>
    public bool RequiresRestart { get; init; }

    /// <summary>Whether it applies on this machine; a setting that doesn't is not shown.</summary>
    public Func<bool> IsAvailable { get; init; } = static () => true;

    /// <summary>
    /// For a setting kept outside RunPad's settings file, in the desktop's own configuration: read and
    /// written there, so a change made in the desktop's settings shows here too.
    /// </summary>
    public ExternalValue? External { get; init; }
}

public sealed record ExternalValue(Func<bool> Read, Action<bool> Write);
