using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RunPad.Services.Secrets;

/// <summary>A named secret: a password, token or key a script reads with <c>Util.GetSecret</c>.</summary>
public sealed record Secret(string Name, string Value);

public static class SecretStoreExtensions
{
    /// <summary>The value of a secret, by name in any case as <c>Util.GetSecret</c> finds it; null when the
    /// store has none of that name or can't be read.</summary>
    public static string? Find(this ISecretStore store, string name)
    {
        try { return store.LoadSnapshot().Secrets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))?.Value; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Secrets and the exact file generation they were read from; null means no file existed.</summary>
public sealed record SecretStoreSnapshot(IReadOnlyList<Secret> Secrets, string? Version);

public sealed class SecretStoreConflictException() : IOException("The secret store changed after it was opened.");

/// <summary>Another RunPad window is saving the store, or a running script is reading it; trying again
/// shortly usually succeeds. Nothing was written.</summary>
public sealed class SecretStoreBusyException(Exception inner) : IOException("The secret store is in use.", inner);

/// <summary>
/// RunPad's Secret Store: secrets kept on this machine for this user, outside every script, so a
/// script names a secret instead of containing it. Edited in Tools → Secret Store.
/// </summary>
public interface ISecretStore
{
    /// <summary>The store's file.</summary>
    string FilePath { get; }

    /// <summary>Every secret, in the order they were saved; empty when there is no store yet.</summary>
    /// <exception cref="InvalidDataException">The file can't be read here: damaged, from a newer RunPad,
    /// or encrypted for another Windows user. It is left as it is.</exception>
    SecretStoreSnapshot LoadSnapshot();

    /// <summary>Replaces the store only if it still matches the loaded version; returns the new version.</summary>
    /// <exception cref="SecretStoreConflictException">Another writer changed the file; nothing was written.</exception>
    /// <exception cref="SecretStoreBusyException">The file stayed in use; nothing was written.</exception>
    string Save(IReadOnlyList<Secret> secrets, string? expectedVersion);
}

/// <summary>
/// The store as one JSON file, by default <c>secrets.json</c> in RunPad's data folder. On Windows each
/// value is encrypted with DPAPI for the current user, as LINQPad's password manager does; on macOS
/// and Linux the values are plain text in a file only its owner can read, like <c>~/.pgpass</c>.
/// <c>RUNPAD_SECRETS_FILE</c> points RunPad and its scripts at another file.
/// </summary>
/// <remarks>
/// RunPad.Runtime reads this file itself for <c>Util.GetSecret</c> (its own copy of the format, as
/// for the data folder): the version, the protection names and the value encoding must stay in step.
/// </remarks>
public sealed class FileSecretStore : ISecretStore
{
    /// <summary>Also read by <c>Util.GetSecret</c> in the script's process.</summary>
    public const string FileEnvVar = "RUNPAD_SECRETS_FILE";

    private const int FormatVersion = 1;
    private const string Dpapi = "DPAPI";
    private const string NoProtection = "None";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public FileSecretStore(string? filePath = null) => FilePath = filePath ?? DefaultPath();

    public string FilePath { get; }

    public static string DefaultPath() =>
        Environment.GetEnvironmentVariable(FileEnvVar) is { Length: > 0 } path
            ? path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RunPad", "secrets.json");

    public IReadOnlyList<Secret> Load() => LoadSnapshot().Secrets;

    public SecretStoreSnapshot LoadSnapshot()
    {
        var bytes = ReadBytes(FilePath);
        if (bytes is null) return new SecretStoreSnapshot([], null);
        StoreFile file;
        try
        {
            file = JsonSerializer.Deserialize<StoreFile>(bytes)
                ?? throw new InvalidDataException("The secret store is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The secret store file is damaged.", ex);
        }
        if (file.Version > FormatVersion)
            throw new InvalidDataException($"The secret store was written by a newer RunPad (format {file.Version}).");
        if (file.Version != FormatVersion)
            throw new InvalidDataException("The secret store file is damaged.");

        // Util.GetSecret finds a secret by name letter case aside, so names differing only in case
        // would make the secret it reads depend on the file's order.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in file.Secrets ?? [])
        {
            if (value is null)
                throw new InvalidDataException("The secret store file is damaged.");
            if (!names.Add(name))
                throw new InvalidDataException($"The secret store file is damaged: two secrets are named '{name}' (letter case aside).");
        }

        var unprotect = file.Protection switch
        {
            NoProtection => (Func<string, string>)(value => value),
            Dpapi when OperatingSystem.IsWindows() => Unprotect,
            Dpapi => throw new InvalidDataException("The secret store is encrypted for a Windows user and can't be read here."),
            _ => throw new InvalidDataException($"The secret store uses an unknown protection ({file.Protection})."),
        };
        try
        {
            return new SecretStoreSnapshot([.. (file.Secrets ?? []).Select(pair => new Secret(pair.Key, unprotect(pair.Value!)))], VersionOf(bytes));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidDataException("The secret store is encrypted for another Windows user or is damaged.", ex);
        }
    }

    public string Save(IReadOnlyList<Secret> secrets, string? expectedVersion)
    {
        // A store that is a symbolic link (into a synced or shared folder, say) stays one: the file it
        // points to is replaced, not the link.
        var fullPath = StoreTarget(Path.GetFullPath(FilePath));
        var folder = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(folder);
        // Keep this lock file: deleting it would let concurrent writers lock different inodes.
        var lockOptions = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var writer = WhileInUse(() => new FileStream(fullPath + ".lock", lockOptions));
        DeleteLeftoverTemps(folder, Path.GetFileName(fullPath));
        var current = ReadBytes(fullPath);
        var currentVersion = current is null ? null : VersionOf(current);
        if (!string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
            throw new SecretStoreConflictException();
        var windows = OperatingSystem.IsWindows();
        var file = new StoreFile
        {
            Version = FormatVersion,
            Protection = windows ? Dpapi : NoProtection,
            Secrets = secrets.ToDictionary(s => s.Name, string? (s) => OperatingSystem.IsWindows() ? Protect(s.Value) : s.Value,
                StringComparer.OrdinalIgnoreCase),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, WriteOptions);

        // Written next to the store and moved over it, so a failed write never leaves half a store;
        // created readable by its owner only, before any secret is in it. Named after the store, so
        // one left by a process killed before the move is recognised and removed by the next save.
        var temp = Path.Combine(folder, TempPrefix(Path.GetFileName(fullPath)) + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!windows) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            ReplaceStore(temp, fullPath);
            return VersionOf(bytes);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // The file a store path finally points to, following symbolic links; the path itself otherwise.
    private static string StoreTarget(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path; // a link cycle: writing over the link reports the problem itself
        }
    }

    private static string TempPrefix(string storeName) => storeName + ".";

    // Called under the lock, so no other save is between writing and moving its own temp file.
    private static void DeleteLeftoverTemps(string folder, string storeName)
    {
        foreach (var path in Directory.EnumerateFiles(folder, TempPrefix(storeName) + "*.tmp"))
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // On Windows a script reading the store (Util.GetSecret) keeps it open for a moment, and replacing
    // an open file fails; it is retried like the lock. Access denied persisting with the file free is a
    // real permission problem and is reported as such.
    private static void ReplaceStore(string temp, string target) => WhileInUse(() =>
    {
        try
        {
            File.Move(temp, target, overwrite: true);
        }
        catch (UnauthorizedAccessException ex) when (OperatingSystem.IsWindows() && IsOpenElsewhere(target))
        {
            throw new IOException(ex.Message, unchecked((int)0x80070020)); // ERROR_SHARING_VIOLATION
        }
        return true;
    });

    private static bool IsOpenElsewhere(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException ex) when (IsInUse(ex)) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // Retries for under a second while the file is held by another writer or reader, then gives up
    // with SecretStoreBusyException; any other failure is thrown at once.
    private static T WhileInUse<T>(Func<T> action)
    {
        const int attempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (IOException ex) when (IsInUse(ex))
            {
                if (attempt == attempts) throw new SecretStoreBusyException(ex);
                Thread.Sleep(50 * attempt);
            }
        }
    }

    // A sharing or lock violation on Windows; on macOS and Linux, .NET's FileShare.None lock failing
    // with EWOULDBLOCK (35 on macOS and the BSDs, 11 on Linux).
    private static bool IsInUse(IOException ex) => OperatingSystem.IsWindows()
        ? (ex.HResult & 0xFFFF) is 32 or 33
        : ex.HResult is 11 or 35;

    private static string VersionOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[]? ReadBytes(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string Protect(string value) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string Unprotect(string value) => Encoding.UTF8.GetString(
        ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));

    private sealed class StoreFile
    {
        public int Version { get; set; }
        public string? Protection { get; set; }
        public Dictionary<string, string?>? Secrets { get; set; }
    }
}
