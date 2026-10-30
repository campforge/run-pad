using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Invoklet.Build.Core.Secrets;

namespace Invoklet.Services.Secrets;

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

    /// <summary>The names of the secrets; from a store that can list them without decrypting, without reading its key.</summary>
    public static IReadOnlyList<string> LoadNames(this ISecretStore store)
        => store is ISecretNames named ? named.LoadNames() : [.. store.LoadSnapshot().Secrets.Select(s => s.Name)];
}

public static class SecretStorePut
{
    /// <summary>
    /// Adds a secret, or replaces the value of one of that name in any case where it stands. Saved
    /// again on the newer store when another window saved first.
    /// </summary>
    public static void Put(this ISecretStore store, string name, string value)
    {
        for (var attempt = 1; ; attempt++)
        {
            var snapshot = store.LoadSnapshot();
            var secrets = snapshot.Secrets.ToList();
            var index = secrets.FindIndex(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) secrets[index] = secrets[index] with { Value = value };
            else secrets.Add(new Secret(name, value));
            try
            {
                store.Save(secrets, snapshot.Version);
                return;
            }
            catch (SecretStoreConflictException) when (attempt < 3) { }
        }
    }
}

/// <summary>Secrets and the exact file generation they were read from; null means no file existed.</summary>
public sealed record SecretStoreSnapshot(IReadOnlyList<Secret> Secrets, string? Version);

public sealed class SecretStoreConflictException() : IOException("The secret store changed after it was opened.");

/// <summary>Another Invoklet window is saving the store, or a running script is reading it; trying again
/// shortly usually succeeds. Nothing was written.</summary>
public sealed class SecretStoreBusyException(Exception inner) : IOException("The secret store is in use.", inner);

/// <summary>
/// A store that can list the names of its secrets without opening what protects their values. The names are not encrypted, so where a list of
/// names is all that is needed (which secrets a connection can use) <see cref="LoadNames"/> is asked, not <see cref="ISecretStore.LoadSnapshot"/>:
/// the key is not read from the system keychain, which may ask the user to unlock it.
/// </summary>
public interface ISecretNames
{
    /// <summary>The names, in the order the secrets were saved; empty when there is no store yet.</summary>
    /// <exception cref="InvalidDataException">The file can't be read here: damaged or from a newer Invoklet.</exception>
    IReadOnlyList<string> LoadNames();
}

/// <summary>
/// Invoklet's Secret Store: secrets kept on this machine for this user, outside every script, so a
/// script names a secret instead of containing it. Edited in Tools → Secret Store.
/// </summary>
public interface ISecretStore
{
    /// <summary>The store's file.</summary>
    string FilePath { get; }

    /// <summary>Every secret, in the order they were saved; empty when there is no store yet.</summary>
    /// <exception cref="InvalidDataException">The file can't be read here: damaged, from a newer Invoklet,
    /// or encrypted for another Windows user. It is left as it is.</exception>
    SecretStoreSnapshot LoadSnapshot();

    /// <summary>Replaces the store only if it still matches the loaded version; returns the new version.</summary>
    /// <exception cref="SecretStoreConflictException">Another writer changed the file; nothing was written.</exception>
    /// <exception cref="SecretStoreBusyException">The file stayed in use; nothing was written.</exception>
    string Save(IReadOnlyList<Secret> secrets, string? expectedVersion);

    /// <summary>Why the values are not encrypted on this machine, when they aren't; null otherwise.</summary>
    string? Warning => null;
}

/// <summary>
/// The store as one JSON file, by default <c>secrets.json</c> in Invoklet's data folder, readable by its
/// owner only. On Windows each value is encrypted with DPAPI for the current user, as LINQPad's
/// password manager does; on macOS and Linux with a key kept in the system keychain
/// (<see cref="SecretKeys"/>). Without a keychain (a Linux machine with no Secret Service) the values
/// stay plain text and <see cref="Warning"/> says so. <c>INVOKLET_SECRETS_FILE</c> points Invoklet and
/// its scripts at another file.
/// </summary>
/// <remarks>
/// Invoklet.Runtime reads this file itself for <c>Util.GetSecret</c> (its own copy of the format, as
/// for the data folder): the version, the protection names and the value encoding must stay in step.
/// </remarks>
public sealed class FileSecretStore : ISecretStore, ISecretNames
{
    /// <summary>Also read by <c>Util.GetSecret</c> in the script's process.</summary>
    public const string FileEnvVar = SecretKeys.FileEnvVar;

    private const int FormatVersion = 1;
    private const string Dpapi = "DPAPI";
    private const string NoProtection = "None";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly ISecretKeySource? _keys;

    /// <param name="filePath">The store's file; Invoklet's own store when null.</param>
    /// <param name="keys">Where the encryption key is kept. Invoklet's own store uses the system
    /// keychain; a store at a given path encrypts only with keys given here (or DPAPI on Windows).</param>
    public FileSecretStore(string? filePath = null, ISecretKeySource? keys = null)
    {
        FilePath = filePath ?? DefaultPath();
        _keys = keys ?? (filePath is null ? SecretKeys.Keychain : null);
    }

    public string FilePath { get; }

    public string? Warning { get; private set; }

    public static string DefaultPath() => SecretKeys.DefaultStorePath();

    /// <summary>
    /// At start: encrypts a store left in plain text (by an Invoklet before encryption, or while the
    /// keychain was unavailable) and passes the key to the scripts this process starts. A store that
    /// can't be read is left for the Secret Store window and <c>Util.GetSecret</c> to report.
    /// </summary>
    public void PrepareForScripts()
    {
        if (_keys is null) return;
        try
        {
            var snapshot = LoadSnapshot(); // exports the key of an encrypted store
            if (SecretKeys.IsEncrypted(FilePath)) return;
            if (snapshot.Secrets.Count > 0)
            {
                Save(snapshot.Secrets, snapshot.Version);
                return;
            }
            // Nothing to encrypt yet: only see whether the keychain answers, creating no key.
            try { _keys.Find(); }
            catch (SecretKeyUnavailableException ex) { Warning = Unencrypted(ex); }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { }
    }

    public IReadOnlyList<Secret> Load() => LoadSnapshot().Secrets;

    public IReadOnlyList<string> LoadNames()
    {
        var bytes = ReadBytes(FilePath);
        return bytes is null ? [] : [.. (ParseFile(bytes).Secrets ?? []).Keys];
    }

    public SecretStoreSnapshot LoadSnapshot()
    {
        var bytes = ReadBytes(FilePath);
        if (bytes is null) return new SecretStoreSnapshot([], null);
        var file = ParseFile(bytes);

        var unprotect = file.Protection switch
        {
            NoProtection => (Func<string, string, string>)((_, value) => value),
            Dpapi when OperatingSystem.IsWindows() => (_, value) =>
            {
                System.Diagnostics.Debug.Assert(OperatingSystem.IsWindows());
                return Unprotect(value);
            }
            ,
            Dpapi => throw new InvalidDataException("The secret store is encrypted for a Windows user and can't be read here."),
            SecretKeys.Protection => KeychainUnprotect(),
            _ => throw new InvalidDataException($"The secret store uses an unknown protection ({file.Protection})."),
        };
        try
        {
            return new SecretStoreSnapshot([.. (file.Secrets ?? []).Select(pair => new Secret(pair.Key, unprotect(pair.Key, pair.Value!)))], VersionOf(bytes));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidDataException(file.Protection == Dpapi
                ? "The secret store is encrypted for another Windows user or is damaged."
                : "The secret store doesn't match the key in the system keychain, or is damaged.", ex);
        }
    }

    // The file as written, checked, with its values still as they are stored: reading it asks nothing of the keychain.
    private static StoreFile ParseFile(byte[] bytes)
    {
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
            throw new InvalidDataException($"The secret store was written by a newer Invoklet (format {file.Version}).");
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
        return file;
    }

    // The key of a store encrypted with one, exported for the scripts this process starts.
    private Func<string, string, string> KeychainUnprotect()
    {
        byte[]? key;
        try
        {
            key = _keys?.Find();
        }
        catch (SecretKeyUnavailableException ex)
        {
            throw new InvalidDataException($"The secret store is encrypted, and its key can't be read: {ex.Message}", ex);
        }
        if (key is null)
            throw new InvalidDataException("The secret store is encrypted with a key that isn't in this system keychain.");
        SecretKeys.ExportForScripts(key);
        return (name, value) => SecretKeys.Decrypt(key, name, value);
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
        // Under the lock, so two Invoklets never create two keys for one store.
        var key = KeyForSave(current is not null && SecretKeys.IsEncrypted(fullPath));
        var file = new StoreFile
        {
            Version = FormatVersion,
            Protection = key is not null ? SecretKeys.Protection : windows ? Dpapi : NoProtection,
            Secrets = secrets.ToDictionary(s => s.Name,
                string? (s) => key is not null ? SecretKeys.Encrypt(key, s.Name, s.Value) : OperatingSystem.IsWindows() ? Protect(s.Value) : s.Value,
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
            if (key is not null) SecretKeys.ExportForScripts(key);
            return VersionOf(bytes);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // An encrypted store keeps its key: one that can't be read stops the save rather than replace
    // values nobody could decrypt any more. A plain one gets a key, or stays plain without a keychain.
    private byte[]? KeyForSave(bool encrypted)
    {
        if (_keys is null) return null;
        if (encrypted)
            return _keys.Find() ?? throw new SecretKeyUnavailableException(
                "The secret store is encrypted with a key that isn't in this system keychain; nothing was written.");
        try
        {
            var key = _keys.GetOrCreate();
            Warning = null;
            return key;
        }
        catch (SecretKeyUnavailableException ex)
        {
            Warning = Unencrypted(ex);
            return null;
        }
    }

    private static string Unencrypted(SecretKeyUnavailableException ex) =>
        $"Secrets are saved unencrypted, readable by your user account only. {ex.Message}";

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
