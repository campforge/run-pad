namespace Invoklet.Runtime;

/// <summary>
/// Per-type <see cref="string.Format(string, object)"/>-style format strings used when rendering
/// numbers/dates in <c>.Dump()</c> output — LINQPad-compatible <c>DumpFormatStrings</c> (signature
/// verified against the real <c>LINQPad.Runtime.dll</c>). <see langword="null"/> on any property means
/// "use the built-in default" for that type (see <see cref="DumpRenderer"/>).
/// </summary>
public sealed record DumpFormatStrings
{
    /// <summary>
    /// Creates a new instance. <paramref name="applyUserDefaults"/> is accepted for LINQPad-signature
    /// compatibility but is currently a no-op — Invoklet has no user-configurable default format-strings
    /// preference (LINQPad's own Options dialog) to apply; every property starts <see langword="null"/>
    /// either way, meaning "use the built-in default" until a script sets one explicitly.
    /// </summary>
    public DumpFormatStrings(bool applyUserDefaults = true)
    {
    }

    /// <summary>Format string for integral numbers (<see cref="sbyte"/>/<see cref="byte"/>/<see cref="short"/>/.../<see cref="long"/>). <see langword="null"/> renders with the current/invariant culture's default.</summary>
    public string? IntegralNumber { get; set; }

    /// <summary>Format string for floating-point/<see cref="decimal"/> numbers. <see langword="null"/> renders with the current/invariant culture's default.</summary>
    public string? RealNumber { get; set; }

    /// <summary>Format string for <see cref="DateTime"/>. <see langword="null"/> renders as round-trip ("o").</summary>
    public string? DateTime { get; set; }

    /// <summary>Format string for <see cref="DateTimeOffset"/>. <see langword="null"/> renders as round-trip ("o").</summary>
    public string? DateTimeOffset { get; set; }

    /// <summary>
    /// Format string for <c>DateOnly</c>. Not referenced by type here — this library targets
    /// netstandard2.0 (predates <c>DateOnly</c>/<c>TimeOnly</c>, added in .NET 6), so
    /// <see cref="DumpRenderer"/> detects/formats values of that type by name via reflection instead
    /// of a direct type reference. <see langword="null"/> renders as round-trip ("o").
    /// </summary>
    public string? DateOnly { get; set; }

    /// <summary>Format string for <c>TimeOnly</c> — see <see cref="DateOnly"/>'s own remarks on why it's not referenced by type. <see langword="null"/> renders as round-trip ("o").</summary>
    public string? TimeOnly { get; set; }
}
