using System.Text.Json;

namespace Invoklet.Services.Recovery;

/// <summary>Private immutable snapshots, an atomic current pointer and one previous generation.
/// A held OS file lock keeps independent application instances out of one another's sessions.</summary>
public sealed class DiskRecoveryStore : IRecoveryStore
{
    private sealed record Pointer(int Version, string Current, string? Previous);
    private readonly string _root;
    private readonly SemaphoreSlim _writer = new(1);
    private readonly object _initializationLock = new();
    private readonly Action<string>? _fault;
    private Task<RecoverySnapshot?>? _load;
    private FileStream? _owner;
    private string? _folder;
    private Pointer? _pointer;
    private long _revision = -1;
    private bool _disposed;
    private readonly List<RecoveryImport> _imports = [];
    private readonly List<FileStream> _importLocks = [];
    // Sessions taken over by this one, carried in every checkpoint (see RecoveryImportRecord).
    private readonly List<RecoveryImportRecord> _records = [];
    // Sessions an imported one had taken over itself: their drafts arrive with its own, so this one
    // records them too, and they stay recognised as taken over once that session is removed.
    private readonly List<RecoveryImportRecord> _inherited = [];
    public string? Warning { get; private set; }

    /// <summary>This instance's own session folder, once <see cref="LoadAsync"/> has opened it.</summary>
    internal string? SessionFolder => Volatile.Read(ref _folder);

    public DiskRecoveryStore(string root) => _root = root;
    internal DiskRecoveryStore(string root, Action<string> fault) : this(root) => _fault = fault;

    public Task<RecoverySnapshot?> LoadAsync()
    {
        lock (_initializationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _load ??= Task.Run(LoadCore);
        }
    }

    private RecoverySnapshot? LoadCore()
    {
        Directory.CreateDirectory(_root);
        RestrictDirectory(_root);
        DiscardStaleSpills();
        // Lock every session nobody else owns before reading any of it: a live instance's session is
        // never read as abandoned. Sessions another one imported (with an import marker) are read
        // too: the marker alone doesn't prove the importing checkpoint is still there (see below).
        var sessions = new List<(string Folder, FileStream Lock, RecoverySnapshot? Snapshot, string? Payload, bool FellBack, Exception? Failure)>();
        foreach (var folder in Directory.GetDirectories(_root)
                     .Where(p => Guid.TryParseExact(Path.GetFileName(p), "N", out _)
                         && (File.Exists(Path.Combine(p, "current.json")) || File.Exists(Path.Combine(p, "previous.json"))))
                     .OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            FileStream owner;
            try { owner = Lock(folder); }
            catch (IOException) { continue; } // Another live instance owns this session.
            catch (UnauthorizedAccessException) { continue; }
            if (sessions.Count == 0) _fault?.Invoke("locked");
            try
            {
                var (snapshot, payload, fellBack) = ReadFolder(folder);
                sessions.Add((folder, owner, snapshot, payload, fellBack, null));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or NotSupportedException or UnauthorizedAccessException)
            {
                sessions.Add((folder, owner, null, null, false, ex));
            }
        }

        // A session whose drafts a durable checkpoint already holds was taken over; if the marker
        // was not written (a crash right after that checkpoint), finish that now. Only the
        // generations actually read count: when the destination's importing checkpoint is lost and
        // it falls back to one from before the import, the drafts are reachable only from the source.
        var records = sessions.SelectMany(x => (x.Snapshot?.ImportedSessions ?? [])
            .Select(record => (Destination: x.Folder, Record: record))).ToList();
        bool TakenOver(string folder, RecoverySnapshot snapshot, string? payload) =>
            records.Any(r => Describes(r.Record, folder, snapshot, payload, r.Destination));
        var failed = sessions.Where(x => x.Failure is not null).Select(x => Path.GetFileName(x.Folder)).ToHashSet();
        var held = sessions.Select(x => Path.GetFileName(x.Folder)).ToHashSet();

        var unavailable = new List<string>();
        var undetermined = new List<string>();
        Exception? unreadable = null;
        RecoverySnapshot? own = null;
        foreach (var session in sessions)
        {
            if (session.Snapshot is { } taken && TakenOver(session.Folder, taken, session.Payload))
            {
                var owner = records.First(r => Describes(r.Record, session.Folder, taken, session.Payload, r.Destination));
                TryMarkImported(session.Folder, owner.Record, owner.Destination);
                session.Lock.Dispose();
            }
            else if (session.Failure is not null)
            {
                // Unreadable or newer data may hold drafts: keep it unchanged and say where it is.
                unreadable ??= session.Failure;
                unavailable.Add(session.Folder);
                session.Lock.Dispose();
            }
            else if (File.Exists(Path.Combine(session.Folder, ImportMarker)))
            {
                // Imported before, but no readable checkpoint records it any more. Where the drafts
                // went decides: a live destination owns them, an unreadable one may still hold them
                // (kept unchanged and reported, never imported a second time), and a destination
                // that is gone or fell back past the import leaves the source as their only copy.
                switch (ImportDestinationState(session.Folder, held, failed))
                {
                    case DestinationState.Live:
                        session.Lock.Dispose();
                        break;
                    case DestinationState.Unknown:
                        undetermined.Add(session.Folder);
                        session.Lock.Dispose();
                        break;
                    default:
                        Offer(session.Folder, session.Lock, session.Snapshot, session.Payload);
                        break;
                }
            }
            else if (_folder is null)
            {
                _folder = session.Folder;
                _owner = session.Lock;
                own = session.Snapshot;
                if (session.Payload is not null) _pointer = new Pointer(1, session.Payload, null);
                _revision = own?.Revision ?? -1;
                if (session.FellBack) Warning = "An earlier draft checkpoint was recovered because the newer one was damaged.";
            }
            else Offer(session.Folder, session.Lock, session.Snapshot, session.Payload);
        }
        _records.AddRange(own?.ImportedSessions ?? []);
        if (unavailable.Count > 0)
            Warning = string.Join("\n", new[] { Warning,
                "Some draft data could not be read and was kept unchanged. Recovery folders:",
                string.Join("\n", unavailable) }.Where(s => !string.IsNullOrEmpty(s)));
        // These were read; it's the session that took their drafts over that can't be checked now.
        if (undetermined.Count > 0)
            Warning = string.Join("\n", new[] { Warning,
                "Some drafts were taken over by another session that can't be checked now; they were kept unchanged and not restored. Recovery folders:",
                string.Join("\n", undetermined) }.Where(s => !string.IsNullOrEmpty(s)));
        if (_folder is null)
        {
            // Unreadable data alone must not be replaced by an empty session that hides it.
            if (unreadable is not null && _imports.Count == 0)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(unreadable);
            _folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            RestrictDirectory(_folder);
            _owner = Lock(_folder);
        }
        return own;
    }

    // Whether a record is about the generation read from this folder. A folder can be reused: an
    // instance that listed it before it was retired may adopt it, empty, as its own new session, and
    // its revisions start again, so only the checkpoint file identifies what was imported. A record
    // without one identifies nothing: matching it by revision could retire a new owner's checkpoints,
    // while not matching at most offers the drafts once more.
    private static bool Describes(RecoveryImportRecord record, string folder, RecoverySnapshot snapshot, string? payload, string destination)
    {
        if (record.Source != Path.GetFileName(folder)) return false;
        if (record.Payload is not { } imported) return false;
        if (imported == payload) return true;
        if (snapshot.Revision >= record.Revision || payload is null) return false;

        // An older readable payload is still this imported session if a pointer links it to the
        // imported generation. Retiring a folder removes both pointers, so a new owner cannot match.
        foreach (var name in new[] { "current.json", "previous.json" })
        {
            try
            {
                var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllBytes(Path.Combine(folder, name)));
                if (pointer is { Version: 1 } && pointer.Current == imported && pointer.Previous == payload)
                    return true;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }

        // A damaged pointer can erase that link. Its import marker corroborates ownership only
        // alongside a readable destination checkpoint holding the same import record. Markers are
        // deleted during retirement too; they never make a reused folder belong to an old record.
        try
        {
            var marker = JsonSerializer.Deserialize<ImportMarkerData>(File.ReadAllBytes(Path.Combine(folder, ImportMarker)));
            return marker is { Version: 1 } && marker.Into == Path.GetFileName(destination) && marker.Revision == record.Revision;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    // Another session's unsaved tabs become an import, held locked until a checkpoint records it.
    private void Offer(string folder, FileStream owner, RecoverySnapshot? snapshot, string? payload)
    {
        var drafts = snapshot?.Tabs
            .Where(t => t.IsDirty || (t.FilePath is null && !string.IsNullOrWhiteSpace(t.EditorText)))
            .Select(t => t with { Id = Guid.NewGuid(), ImportedFrom = Path.GetFileName(folder) })
            .ToList() ?? [];
        if (drafts.Count == 0) { owner.Dispose(); return; } // Nothing unsaved there.
        _imports.Add(new RecoveryImport(Path.GetFileName(folder), snapshot!.Revision, drafts, payload));
        _inherited.AddRange(snapshot.ImportedSessions ?? []);
        _importLocks.Add(owner);
    }

    private enum DestinationState { Gone, Live, Unknown }

    // The session an import marker names. One read in this start without the record, or missing
    // altogether, is Gone; one another instance holds is Live; an unreadable one, or a marker that
    // can't be read, is Unknown.
    private DestinationState ImportDestinationState(string source, HashSet<string> held, HashSet<string> failed)
    {
        string into;
        try
        {
            var marker = JsonSerializer.Deserialize<ImportMarkerData>(File.ReadAllBytes(Path.Combine(source, ImportMarker)));
            if (marker is null || marker.Version != 1 || !Guid.TryParseExact(marker.Into, "N", out _)) return DestinationState.Unknown;
            into = marker.Into;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return DestinationState.Unknown; }
        if (failed.Contains(into)) return DestinationState.Unknown;
        if (held.Contains(into)) return DestinationState.Gone; // read here, and it no longer records the import
        var folder = Path.Combine(_root, into);
        if (!File.Exists(Path.Combine(folder, "current.json")) && !File.Exists(Path.Combine(folder, "previous.json")))
            return DestinationState.Gone;
        try { using var probe = Lock(folder); }
        catch (IOException) { return DestinationState.Live; }
        catch (UnauthorizedAccessException) { return DestinationState.Unknown; }
        return DestinationState.Unknown; // has checkpoints but wasn't among the sessions read: don't guess
    }

    public IReadOnlyList<RecoveryImport> Imports => [.. _imports];

    private const string ImportMarker = "imported.json";

    private sealed record ImportMarkerData(int Version, string Into, long Revision);

    private static void TryMarkImported(string folder, RecoveryImportRecord record, string destination)
    {
        // The drafts are already in a durable checkpoint, so a missing marker only means they are
        // filtered out again on the next start. The source session's own files are not changed here;
        // it is removed only once both generations hold its drafts (TryRemoveTakenOver).
        // Replaced atomically, so after a second import it names the session that holds them now.
        var temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WriteDurably(temp, JsonSerializer.SerializeToUtf8Bytes(
                new ImportMarkerData(1, Path.GetFileName(destination), record.Revision)));
            File.Move(temp, Path.Combine(folder, ImportMarker), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Deletes the checkpoints of a session whose drafts this one holds, as of the generation that was
    // imported; true once the record is no longer needed. A session someone has open is left for a
    // later checkpoint; a folder holding anything but that generation is not the record's any more.
    private bool TryRemoveTakenOver(RecoveryImportRecord record)
    {
        if (!Guid.TryParseExact(record.Source, "N", out _)) return false;
        var folder = Path.Combine(_root, record.Source);
        if (!File.Exists(Path.Combine(folder, "current.json")) && !File.Exists(Path.Combine(folder, "previous.json")))
            return true;
        try
        {
            using (Lock(folder))
            {
                var (snapshot, payload, _) = ReadFolder(folder);
                if (snapshot is not null && !Describes(record, folder, snapshot, payload, _folder!)) return true;
                // The pointers first: without them the folder is no session any more, so an
                // interrupted removal leaves nothing that is loaded or offered again.
                File.Delete(Path.Combine(folder, "current.json"));
                File.Delete(Path.Combine(folder, "previous.json"));
                foreach (var file in Directory.EnumerateFiles(folder).Where(f => Path.GetFileName(f) != "owner.lock"))
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or NotSupportedException or UnauthorizedAccessException)
        {
            return !File.Exists(Path.Combine(folder, "current.json")) && !File.Exists(Path.Combine(folder, "previous.json"));
        }
        _fault?.Invoke("import-retired");
        // Keep the lock inode and its empty directory. After releasing ownership another instance
        // may already have adopted the folder; deleting anything here could destroy its new data
        // or split ownership across two different lock files. Folders without pointers are ignored.
        return true;
    }

    internal static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // What an instance keeps of the tabs it released is its own and ends with it. The folder of a session
    // that is not locked by anyone belongs to an instance that is gone (it may even be adopted by this one
    // as its own), so whatever it left in its spill is old, whoever's it was.
    private void DiscardStaleSpills()
    {
        foreach (var folder in Directory.GetDirectories(_root).Where(p => Guid.TryParseExact(Path.GetFileName(p), "N", out _)))
        {
            var spill = Path.Combine(folder, SessionSpillStore.FolderName);
            if (!Directory.Exists(spill)) continue;
            FileStream owner;
            try { owner = Lock(folder); }
            catch (IOException) { continue; } // A live instance owns this session.
            catch (UnauthorizedAccessException) { continue; }
            using (owner) TryDeleteDirectory(spill);
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only data that is cached elsewhere is left behind.
        }
    }

    private static FileStream Lock(string folder) => new(Path.Combine(folder, "owner.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static (RecoverySnapshot? Snapshot, string? Payload, bool FellBack) ReadFolder(string folder)
    {
        Exception? failure = null;
        foreach (var name in new[] { "current.json", "previous.json" })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            try
            {
                var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllBytes(path))
                    ?? throw new InvalidDataException("Invalid recovery pointer.");
                if (pointer.Version != 1) throw new NotSupportedException("Unsupported recovery version. Data has been kept unchanged.");
                if (string.IsNullOrEmpty(pointer.Current)) throw new InvalidDataException("Missing recovery payload.");
                foreach (var payload in new[] { pointer.Current, pointer.Previous })
                {
                    if (payload is null) continue;
                    try
                    {
                        ValidatePayloadName(payload);
                        var snapshot = JsonSerializer.Deserialize<RecoverySnapshot>(File.ReadAllBytes(Path.Combine(folder, payload)))
                            ?? throw new InvalidDataException("Invalid recovery snapshot.");
                        Validate(snapshot);
                        return (snapshot, payload, failure is not null);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException) { failure = ex; }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException) { failure = ex; }
        }
        if (failure is not null) throw new InvalidDataException("Recovery data could not be read. It has been kept unchanged.");
        return (null, null, false);
    }

    private static void ValidatePayloadName(string name)
    {
        if (!name.EndsWith(".snapshot", StringComparison.Ordinal)
            || !Guid.TryParseExact(name[..^9], "N", out _))
            throw new InvalidDataException("Invalid recovery payload name.");
    }

    private static void Validate(RecoverySnapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1) throw new NotSupportedException("Unsupported recovery version. Data has been kept unchanged.");
        if (snapshot.Revision < 0 || snapshot.Tabs is null
            || snapshot.Tabs.Any(t => t is null)
            || snapshot.Tabs.Select(t => t.Id).Distinct().Count() != snapshot.Tabs.Count
            || snapshot.Tabs.Any(t => t.Id == Guid.Empty || t.EditorText is null
                || (t.Document is { } doc && (doc.FrontMatter is null || doc.RunCode is null
                    || doc.BodyBeforeCode is null || doc.BodyAfterCode is null || doc.FrontMatter.Packages is null
                    || doc.FrontMatter.TargetSdk is null))
                || (t.FilePath is not null && !Path.IsPathFullyQualified(t.FilePath)))
            || (snapshot.ActiveTab is { } active && !snapshot.Tabs.Any(t => t.Id == active)))
            throw new InvalidDataException("Invalid or unsupported recovery snapshot.");
    }

    public async Task CommitAsync(RecoverySnapshot snapshot)
    {
        Validate(snapshot);
        await LoadAsync().ConfigureAwait(false);
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (snapshot.Revision <= _revision) return;
            // Drafts imported at startup are in this workspace from the restore on: every checkpoint
            // records their sessions, and only once one is durable are those sessions marked.
            var pending = _imports.Select(i => new RecoveryImportRecord(i.Source, i.Revision, i.Payload))
                .Concat(_inherited).Except(_records).Distinct().ToList();
            // Recorded by the checkpoint this one replaces, so in both generations a load can fall
            // back to once this one is durable.
            var settled = _records.ToList();
            snapshot = snapshot with { ImportedSessions = [.. _records, .. pending] };
            await Task.Run(() =>
            {
                var name = Guid.NewGuid().ToString("N") + ".snapshot";
                WriteDurably(Path.Combine(_folder!, name), JsonSerializer.SerializeToUtf8Bytes(snapshot));
                _fault?.Invoke("payload");
                var pointer = new Pointer(1, name, _pointer?.Current);
                if (_pointer is not null)
                    ReplacePointer("previous.json", JsonSerializer.SerializeToUtf8Bytes(_pointer));
                _fault?.Invoke("previous");
                ReplacePointer("current.json", JsonSerializer.SerializeToUtf8Bytes(pointer));
                _pointer = pointer;
                _revision = snapshot.Revision;
                _fault?.Invoke("committed");
                foreach (var record in pending)
                    TryMarkImported(Path.Combine(_root, record.Source), record, _folder!);
                _records.AddRange(pending);
                _imports.Clear();
                _inherited.Clear();
                // Drafts both reachable generations hold no longer need the session they came from;
                // once it is gone, later checkpoints stop recording it.
                foreach (var record in settled)
                    if (TryRemoveTakenOver(record)) _records.Remove(record);
                foreach (var held in _importLocks) held.Dispose();
                _importLocks.Clear();
                // Only unreachable generations are disposable; a failed cleanup must not turn a
                // successfully committed checkpoint into a failed save.
                foreach (var file in Directory.EnumerateFiles(_folder!, "*.snapshot"))
                {
                    if (Path.GetFileName(file) == pointer.Current || Path.GetFileName(file) == pointer.Previous) continue;
                    try { File.Delete(file); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }

    private void ReplacePointer(string name, byte[] bytes)
    {
        var temp = Path.Combine(_folder!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WriteDurably(temp, bytes);
            _fault?.Invoke(name);
            File.Move(temp, Path.Combine(_folder!, name), overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void WriteDurably(string path, byte[] bytes)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        Task<RecoverySnapshot?>? loading;
        lock (_initializationLock)
        {
            if (_disposed) return;
            _disposed = true;
            loading = _load;
        }
        // Loading acquires the session lock on a worker; do not leave a late owner behind.
        if (loading is not null)
        {
            try { loading.GetAwaiter().GetResult(); }
            catch (Exception) { /* Failed loads still release any acquired owner below. */ }
        }
        _writer.Wait();
        try
        {
            _owner?.Dispose();
            foreach (var held in _importLocks) held.Dispose();
        }
        finally { _writer.Release(); }
    }
}
