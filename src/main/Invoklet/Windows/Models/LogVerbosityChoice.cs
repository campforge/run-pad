using Invoklet.Services.Preferences;

namespace Invoklet.Windows.Models;

/// <summary>One line of the Logging page's list: a level and what it means for the user.</summary>
public sealed record LogVerbosityChoice(LogVerbosity Value, string Title)
{
    public override string ToString() => Title;

    public static IReadOnlyList<LogVerbosityChoice> All { get; } =
    [
        new(LogVerbosity.Off, "Off: nothing is logged"),
        new(LogVerbosity.Fatal, "Fatal errors only: crashes of Invoklet itself"),
        new(LogVerbosity.Error, "Errors"),
        new(LogVerbosity.Warning, "Warnings and errors"),
        new(LogVerbosity.Information, "Information (default)"),
        new(LogVerbosity.Debug, "Debug: everything, including parts of your scripts"),
    ];
}
