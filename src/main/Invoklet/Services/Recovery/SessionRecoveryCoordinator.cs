namespace Invoklet.Services.Recovery;

public sealed record LoadedRecoverySession(
    RecoverySnapshot? Snapshot,
    IReadOnlyList<RecoveryImport> Imports,
    string? Warning);

public interface ISessionRecoveryCoordinator
{
    Task<LoadedRecoverySession> LoadAsync();
    Task CommitAsync(RecoverySnapshot snapshot);
}

/// <summary>Coordinates recovery-store I/O without knowing anything about editor tabs or UI.</summary>
public sealed class SessionRecoveryCoordinator(IRecoveryStore store, ISessionSnapshotLoader loader)
    : ISessionRecoveryCoordinator
{
    public async Task<LoadedRecoverySession> LoadAsync()
    {
        var snapshot = await store.LoadAsync().ConfigureAwait(false);
        if (snapshot is not null)
            snapshot = snapshot with { Tabs = await loader.LoadAsync(snapshot.Tabs).ConfigureAwait(false) };
        return new LoadedRecoverySession(snapshot, store.Imports ?? [], store.Warning);
    }

    public Task CommitAsync(RecoverySnapshot snapshot)
        => store.CommitAsync(snapshot);
}
