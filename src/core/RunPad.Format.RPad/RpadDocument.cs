namespace RunPad.Format.RPad;

public sealed class RpadDocument
{
    public required RpadFrontMatter FrontMatter { get; init; }

    /// <summary>Code from the run block — the only part the user edits.</summary>
    public string RunCode { get; set; } = "";

    // Everything in the markdown body before the run-block code (including opening fence + newline)
    internal string BodyBeforeCode { get; init; } = "";
    // Everything in the markdown body from the closing ``` onwards
    internal string BodyAfterCode { get; init; } = "";
    /// <summary>A detached copy for local recovery, without serializing or normalizing the document.</summary>
    public RpadDocumentSnapshot CaptureSnapshot() => new(CloneMetadata(FrontMatter), RunCode, BodyBeforeCode, BodyAfterCode);

    public static RpadDocument FromSnapshot(RpadDocumentSnapshot snapshot) => new()
    {
        FrontMatter = CloneMetadata(snapshot.FrontMatter),
        RunCode = snapshot.RunCode,
        BodyBeforeCode = snapshot.BodyBeforeCode,
        BodyAfterCode = snapshot.BodyAfterCode,
    };

    private static RpadFrontMatter CloneMetadata(RpadFrontMatter source) => new()
    {
        Id = source.Id,
        Version = source.Version,
        Language = source.Language,
        TargetSdk = source.TargetSdk,
        Kind = source.Kind,
        Packages = source.Packages.ToDictionary(p => p.Key, p => p.Value),
        References = source.References?.ToList(),
        Namespaces = source.Namespaces?.ToList(),
        Arguments = [.. source.Arguments.Select(a => a.Clone())],
        ArgumentSets = source.ArgumentSets.ToDictionary(s => s.Key,
            s => s.Value.ToDictionary(v => v.Key, v => v.Value.Clone(), StringComparer.Ordinal), StringComparer.Ordinal),
        SelectedArgumentSet = source.SelectedArgumentSet,
        AllowUnsafe = source.AllowUnsafe,
        Connections = [.. source.Connections.Select(c => new RpadConnection
        {
            Name = c.Name,
            Driver = c.Driver,
            Params = c.Params.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal),
            Extra = c.Extra?.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal),
        })],
        UnknownEntries = [.. source.UnknownEntries],
    };
}

public sealed record RpadDocumentSnapshot(RpadFrontMatter FrontMatter, string RunCode, string BodyBeforeCode, string BodyAfterCode);
