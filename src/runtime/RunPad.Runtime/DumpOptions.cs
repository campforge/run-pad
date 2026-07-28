namespace RunPad.Runtime;

/// <summary>
/// Full set of rendering options for a single <c>.Dump()</c> call — LINQPad-compatible
/// <c>DumpOptions</c> (signature verified against the real <c>LINQPad.Runtime.dll</c>). Everyday calls
/// should prefer the plain <c>Dump()</c>/<c>Dump(description)</c>/<c>Dump(depth)</c> overloads instead;
/// reach for this only when you need one of the options below, or want to build on
/// <see cref="Util.DumpDefaults"/> via <see cref="CombineWith"/>.
/// </summary>
public sealed record DumpOptions : DumpOptionsCore
{
    /// <summary>Creates an options instance with every property unset (falls back entirely to <see cref="Util.DumpDefaults"/>/RunPad's built-in defaults).</summary>
    public DumpOptions()
    {
    }

    /// <summary>Creates an options instance with just <see cref="Description"/> set.</summary>
    public DumpOptions(string? description) => Description = description;

    /// <summary>Creates an options instance with just <see cref="DumpOptionsCore.ToDataGrid"/> and (optionally) <see cref="Description"/> set.</summary>
    public DumpOptions(bool toDataGrid, string? description = null)
    {
        ToDataGrid = toDataGrid;
        Description = description;
    }

    /// <summary>Heading shown above the rendered value. <see langword="null"/> renders no heading.</summary>
    public string? Description { get; set; }

    /// <summary>Comma-delimited member names to render — applied before <see cref="MembersToExclude"/>. <see langword="null"/> renders every member.</summary>
    public string? MembersToInclude { get; set; }

    /// <summary>Comma-delimited member names to hide from the rendered property grid.</summary>
    public string? MembersToExclude { get; set; }

    /// <summary>The process-wide default options every <c>.Dump()</c> call starts from — same instance as <see cref="Util.DumpDefaults"/>. A script can mutate its properties directly to change the default for the rest of the run.</summary>
    public static DumpOptionsCore Default => GlobalDefaults;

    // Real LINQPad instance backing both Util.DumpDefaults and DumpOptions.Default — DumpOptions (not
    // just DumpOptionsCore) so it can also be the receiver of CombineWith below, even though it's only
    // ever exposed through the two public properties above at the DumpOptionsCore base-class level.
    internal static readonly DumpOptions GlobalDefaults = new();

    /// <summary>
    /// Returns a new <see cref="DumpOptions"/> layering <paramref name="other"/> on top of this
    /// instance: wherever <paramref name="other"/> leaves a nullable property unset, this instance's
    /// value is kept; a non-nullable (plain <see cref="bool"/>) property always takes
    /// <paramref name="other"/>'s value. Typically used as <c>Util.DumpDefaults.CombineWith(perCallOptions)</c>
    /// to apply a single call's overrides on top of the process-wide defaults. No decompiled
    /// implementation was available to verify the exact original merge rule against — this is a
    /// reasonable, self-consistent interpretation of "combine", not a byte-for-byte port.
    /// </summary>
    public DumpOptions CombineWith(DumpOptions other) => new()
    {
        MaxDepth = other.MaxDepth ?? MaxDepth,
        CollapseTo = other.CollapseTo ?? CollapseTo,
        ToDataGrid = other.ToDataGrid,
        AlphabetizeMembers = other.AlphabetizeMembers ?? AlphabetizeMembers,
        NoTotals = other.NoTotals,
        RepeatHeadersAt = other.RepeatHeadersAt ?? RepeatHeadersAt,
        IncludePrivateMembers = other.IncludePrivateMembers,
        MaxRows = other.MaxRows ?? MaxRows,
        ForceExpansion = other.ForceExpansion,
        UseInvariantCultureForNumbers = other.UseInvariantCultureForNumbers ?? UseInvariantCultureForNumbers,
        FormatStrings = other.FormatStrings,
        Description = other.Description ?? Description,
        MembersToInclude = other.MembersToInclude ?? MembersToInclude,
        MembersToExclude = other.MembersToExclude ?? MembersToExclude,
    };
}
