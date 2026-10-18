using System.Text.Json;

namespace RunPad.Services.Recovery;

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
    public string? Warning { get; private set; }

    public DiskRecoveryStore(string root) => _root = root;
    internal DiskRecoveryStore(string root, Action<string> fault) : this(root) => _fault = fault;

    public Task<RecoverySnapshot?> LoadAsync()
    {
        lock (_initializationLock) return _load ??= Task.Run(LoadCore);
    }

    private RecoverySnapshot? LoadCore()
    {
        Directory.CreateDirectory(_root);
        RestrictDirectory(_root);
        foreach (var folder in Directory.GetDirectories(_root)
                     .Where(p => Guid.TryParseExact(Path.GetFileName(p), "N", out _)
                         && (File.Exists(Path.Combine(p, "current.json")) || File.Exists(Path.Combine(p, "previous.json"))))
                     .OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            try { _owner = Lock(folder); }
            catch (IOException) { continue; } // Another live instance owns this session.
            _folder = folder;
            return ReadSession();
        }
        _folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        RestrictDirectory(_folder);
        _owner = Lock(_folder);
        return null;
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static FileStream Lock(string folder) => new(Path.Combine(folder, "owner.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private RecoverySnapshot? ReadSession()
    {
        Exception? failure = null;
        foreach (var name in new[] { "current.json", "previous.json" })
        {
            var path = Path.Combine(_folder!, name);
            if (!File.Exists(path)) continue;
            try
            {
                var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllBytes(path))
                    ?? throw new InvalidDataException("Invalid recovery pointer.");
                if (pointer.Version != 1) throw new NotSupportedException("Unsupported recovery version. Data has been kept unchanged.");
                foreach (var payload in new[] { pointer.Current, pointer.Previous })
                {
                    if (payload is null) continue;
                    try
                    {
                        ValidatePayloadName(payload);
                        var snapshot = JsonSerializer.Deserialize<RecoverySnapshot>(File.ReadAllBytes(Path.Combine(_folder!, payload)))
                            ?? throw new InvalidDataException("Invalid recovery snapshot.");
                        Validate(snapshot);
                        if (failure is not null) Warning = "An earlier draft checkpoint was recovered because the newer one was damaged.";
                        _pointer = new Pointer(1, payload, null);
                        _revision = snapshot.Revision;
                        return snapshot;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException) { failure = ex; }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException) { failure = ex; }
        }
        if (failure is not null) throw new InvalidDataException("Recovery data could not be read. It has been kept unchanged.");
        return null;
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
            || snapshot.Tabs.Select(t => t.Id).Distinct().Count() != snapshot.Tabs.Count
            || snapshot.Tabs.Any(t => t is null || t.Id == Guid.Empty || t.EditorText is null
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
        await LoadAsync();
        await _writer.WaitAsync();
        try
        {
            if (snapshot.Revision <= _revision) return;
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
                // Only unreachable generations are disposable; a failed cleanup must not turn a
                // successfully committed checkpoint into a failed save.
                foreach (var file in Directory.EnumerateFiles(_folder!, "*.snapshot"))
                {
                    if (Path.GetFileName(file) == pointer.Current || Path.GetFileName(file) == pointer.Previous) continue;
                    try { File.Delete(file); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            });
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
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        _writer.Wait();
        try { _owner?.Dispose(); }
        finally { _writer.Release(); }
    }
}
