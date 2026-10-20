using RunPad.Format.RPad;

namespace RunPad.Services.Recovery;

public sealed record RecoveryTab
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? FilePath { get; init; }
    public string? Title { get; init; }
    public required string EditorText { get; init; }
    public RpadDocumentSnapshot? Document { get; init; }
    public string? ParseError { get; init; }
    public bool IsDirty { get; init; }
    public bool BaselineKnown { get; init; }
    public byte[]? DiskHash { get; init; }
    public string? MetadataBaseline { get; init; }
    public int CaretOffset { get; init; }
    public int SelectionStart { get; init; }
    public int SelectionLength { get; init; }

    /// <summary>The file this draft was edited from, for a draft imported as an untitled copy because
    /// another tab already had that file open.</summary>
    public string? OriginalPath { get; init; }

    /// <summary>The session folder name a draft was imported from (another RunPad window's), or null.</summary>
    public string? ImportedFrom { get; init; }
}

/// <summary>A session whose drafts this one has taken over, at that session's revision: the record
/// travels with every later checkpoint, so the same drafts are never imported twice.</summary>
public sealed record RecoveryImportRecord(string Source, long Revision);

/// <summary>Drafts of another RunPad session nobody owns any more, offered to the starting one.</summary>
/// <param name="Source">The session's folder name.</param>
/// <param name="Revision">The revision those drafts were read at.</param>
/// <param name="Tabs">Its tabs with unsaved changes.</param>
public sealed record RecoveryImport(string Source, long Revision, IReadOnlyList<RecoveryTab> Tabs);

public sealed record RecoverySnapshot
{
    [System.Text.Json.Serialization.JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public long Revision { get; init; }
    public Guid? ActiveTab { get; init; }
    public required IReadOnlyList<RecoveryTab> Tabs { get; init; }

    /// <summary>The other sessions whose drafts this one imported (see <see cref="RecoveryImport"/>).</summary>
    public IReadOnlyList<RecoveryImportRecord>? ImportedSessions { get; init; }
}

public interface IRecoveryStore : IDisposable
{
    /// <summary>What could not be loaded and was kept unchanged, for a non-modal notice; null if nothing.</summary>
    string? Warning => null;

    /// <summary>After <see cref="LoadAsync"/>: drafts of other sessions nobody owns. Their sessions stay
    /// locked until the first successful <see cref="CommitAsync"/>, which records the imports and marks
    /// those sessions as taken over; imports no commit recorded are offered again next time. A later
    /// commit, once both checkpoint generations hold the drafts, deletes those sessions.</summary>
    IReadOnlyList<RecoveryImport> Imports => [];

    Task<RecoverySnapshot?> LoadAsync();
    Task CommitAsync(RecoverySnapshot snapshot);

}
