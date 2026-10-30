namespace Invoklet.Runtime;

/// <summary>
/// The rendering knobs shared between a single <c>.Dump()</c> call's own <see cref="DumpOptions"/> and
/// the process-wide <see cref="Util.DumpDefaults"/> — LINQPad-compatible <c>DumpOptionsCore</c>
/// (signature verified against the real <c>LINQPad.Runtime.dll</c>; every property here is
/// <see langword="null"/>-able so "not set on this particular options instance, fall back to the next
/// layer" is representable — see <see cref="DumpOptions.CombineWith"/>). Invoklet's own built-in
/// fallbacks (depth 3, 1000 rows, current culture, ...) apply only once nothing in the whole chain set
/// a value — see <see cref="DumpRenderer"/>.
/// </summary>
public record DumpOptionsCore
{
    /// <summary>How many levels of nested objects/collections to expand before truncating. <see langword="null"/> falls back to the next layer, ultimately Invoklet's built-in default of 3.</summary>
    public int? MaxDepth { get; set; }

    /// <summary>If set, nested objects deeper than this level render collapsed (still expandable by clicking) instead of always expanded.</summary>
    public int? CollapseTo { get; set; }

    /// <summary>Renders a collection/table as a data grid instead of the default read-only property/row layout. Accepted for LINQPad-signature compatibility; Invoklet currently renders both the same way.</summary>
    public bool ToDataGrid { get; set; }

    /// <summary>Renders members alphabetically instead of in declaration order. <see langword="null"/> falls back to the next layer.</summary>
    public bool? AlphabetizeMembers { get; set; }

    /// <summary>Suppresses the row-count footer under a rendered table.</summary>
    public bool NoTotals { get; set; }

    /// <summary>Repeats the table header every N rows, for long tables. <see langword="null"/> means never.</summary>
    public int? RepeatHeadersAt { get; set; }

    /// <summary>Also renders non-public properties/fields.</summary>
    public bool IncludePrivateMembers { get; set; }

    /// <summary>Maximum number of rows rendered for a collection/table before truncating. <see langword="null"/> falls back to the next layer, ultimately Invoklet's built-in default of 1000.</summary>
    public int? MaxRows { get; set; }

    /// <summary>Renders already expanded even for types that default to collapsed.</summary>
    public bool ForceExpansion { get; set; }

    /// <summary>Formats numbers with <see cref="System.Globalization.CultureInfo.InvariantCulture"/> instead of the current culture. <see langword="null"/> falls back to the next layer, ultimately Invoklet's built-in default of the current culture.</summary>
    public bool? UseInvariantCultureForNumbers { get; set; }

    /// <summary>Per-type format strings for numbers/dates — see <see cref="DumpFormatStrings"/>.</summary>
    public DumpFormatStrings FormatStrings { get; set; } = new();
}
