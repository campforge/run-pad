using RunPad.Format.RPad;

namespace RunPad.Services.Recovery;

public sealed record RecoveryTab
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? FilePath { get; init; }
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
}

public sealed record RecoverySnapshot
{
    [System.Text.Json.Serialization.JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public long Revision { get; init; }
    public Guid? ActiveTab { get; init; }
    public required IReadOnlyList<RecoveryTab> Tabs { get; init; }
}

public interface IRecoveryStore : IDisposable
{
    string? Warning => null;
    Task<RecoverySnapshot?> LoadAsync();
    Task CommitAsync(RecoverySnapshot snapshot);
}
