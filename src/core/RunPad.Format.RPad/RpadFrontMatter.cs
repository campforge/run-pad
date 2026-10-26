namespace RunPad.Format.RPad;

public record TargetSdk(string Name, string? Version = null)
{
    public override string ToString() => $"{Name}{Version}";

    public static readonly TargetSdk Default = new("latest");

    // Splits "net8.0" into Name "net" / Version "8.0"; a value with no digits (e.g. "latest")
    // becomes Name-only.
    public static TargetSdk Parse(string value)
    {
        if (string.IsNullOrEmpty(value)) return Default;
        var idx = 0;
        while (idx < value.Length && !char.IsDigit(value[idx])) idx++;
        return idx == value.Length ? new TargetSdk(value) : new TargetSdk(value[..idx], value[idx..]);
    }
};

public record Package(string Version)
{
    public override string ToString() => $"{Version}";
    public static readonly Package Default = new("latest");
};

public sealed class RpadFrontMatter
{
    /// <summary>
    /// A stable identity for the document, generated once when the file is first created or saved
    /// and preserved across ordinary saves. Save As creates a new ID (the copy is a separate document).
    /// Legacy files without an ID receive one on their first save. Null only while parsing an older
    /// file that has not been saved yet.
    /// </summary>
    public Guid? Id { get; set; }

    public string Version { get; set; } = "1.0";
    public RpadLanguage Language { get; set; } = RpadLanguage.CSharp;
    public TargetSdk TargetSdk { get; set; } = TargetSdk.Default;
    public RpadKind Kind { get; set; } = RpadKind.Statements;
    public Dictionary<string, Package> Packages { get; set; } = new();
    public List<string>? References { get; set; }
    public List<string>? Namespaces { get; set; }

    /// <summary>The script's command-line arguments as written, each one item of <c>args</c>. With
    /// declared <see cref="Arguments"/>, they follow those (see <see cref="RpadArguments"/>).</summary>
    public List<string>? Args { get; set; }

    /// <summary><c>arguments:</c> the arguments the script declares, with their types and defaults, in
    /// the file's order. A run passes each as <c>--name value</c>.</summary>
    public List<RpadArgument> Arguments { get; set; } = [];

    /// <summary>
    /// <c>argumentSets:</c> named sets of values for the declared arguments, in the file's order, keyed
    /// by argument name. An argument a set doesn't give takes its default.
    /// </summary>
    public Dictionary<string, Dictionary<string, RpadValue>> ArgumentSets { get; set; } = new(StringComparer.Ordinal);

    /// <summary><c>selectedArgumentSet:</c> the set runs use; null for the defaults.</summary>
    public string? SelectedArgumentSet { get; set; }

    /// <summary>
    /// <c>allowUnsafe:</c> compiles the script with unsafe code allowed (C#'s /unsafe), for pointers,
    /// <c>sizeof</c> of a user struct and other unsafe contexts. Off unless the script asks for it. A
    /// compiler setting only: no sandbox exists either way, a script always runs with the user's own
    /// permissions. Languages without unsafe code ignore it.
    /// </summary>
    public bool AllowUnsafe { get; set; }

    /// <summary>
    /// <c>connections:</c> the databases, HTTP and gRPC endpoints the script uses, in the file's
    /// order. A file with any is written as format version 1.1.
    /// </summary>
    public List<RpadConnection> Connections { get; set; } = [];

    /// <summary>
    /// Top-level keys this version of RunPad doesn't know, each as its YAML text, written back after
    /// the known ones so a newer RunPad's settings survive a save here.
    /// </summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public List<string> UnknownEntries { get; set; } = [];
}
