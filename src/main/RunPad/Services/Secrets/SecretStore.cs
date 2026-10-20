using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RunPad.Services.Secrets;

/// <summary>A named secret: a password, token or key a script reads with <c>Util.GetSecret</c>.</summary>
public sealed record Secret(string Name, string Value);

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
    IReadOnlyList<Secret> Load();

    /// <summary>Replaces the whole store with <paramref name="secrets"/>.</summary>
    void Save(IReadOnlyList<Secret> secrets);
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

    public IReadOnlyList<Secret> Load()
    {
        if (!File.Exists(FilePath)) return [];
        StoreFile file;
        try
        {
            file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllBytes(FilePath))
                ?? throw new InvalidDataException("The secret store is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The secret store file is damaged.", ex);
        }
        if (file.Version != FormatVersion)
            throw new InvalidDataException($"The secret store was written by a newer RunPad (format {file.Version}).");

        var unprotect = file.Protection switch
        {
            NoProtection => (Func<string, string>)(value => value),
            Dpapi when OperatingSystem.IsWindows() => Unprotect,
            Dpapi => throw new InvalidDataException("The secret store is encrypted for a Windows user and can't be read here."),
            _ => throw new InvalidDataException($"The secret store uses an unknown protection ({file.Protection})."),
        };
        try
        {
            return [.. (file.Secrets ?? []).Select(pair => new Secret(pair.Key, unprotect(pair.Value)))];
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidDataException("The secret store is encrypted for another Windows user or is damaged.", ex);
        }
    }

    public void Save(IReadOnlyList<Secret> secrets)
    {
        var windows = OperatingSystem.IsWindows();
        var file = new StoreFile
        {
            Version = FormatVersion,
            Protection = windows ? Dpapi : NoProtection,
            Secrets = secrets.ToDictionary(s => s.Name, s => OperatingSystem.IsWindows() ? Protect(s.Value) : s.Value,
                StringComparer.OrdinalIgnoreCase),
        };
        var folder = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(folder);

        // Written next to the store and moved over it, so a failed write never leaves half a store;
        // created readable by its owner only, before any secret is in it.
        var temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!windows) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(stream, file, WriteOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
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
        public Dictionary<string, string>? Secrets { get; set; }
    }
}
