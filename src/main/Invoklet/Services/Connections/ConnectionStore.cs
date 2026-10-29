using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Invoklet.Format.RPad;

namespace Invoklet.Services.Connections;

/// <summary>
/// The user's own connections (Tools → Connections), kept by Invoklet outside every script, in the
/// same shape as a script's <c>connections:</c>. Secrets are not in it: their values stay in the
/// Secret Store and connections name them with <c>${secret:…}</c>.
/// </summary>
public sealed record ConnectionStoreSnapshot(IReadOnlyList<RpadConnection> Connections, string? Version);

public sealed class ConnectionStoreConflictException() : IOException("The connection list changed after it was opened.");

public interface IConnectionStore
{
    /// <summary>The store's file.</summary>
    string FilePath { get; }

    /// <summary>Every connection, in the saved order; empty when there is no list yet.</summary>
    /// <exception cref="InvalidDataException">The file is damaged or from a newer Invoklet; it is left as it is.</exception>
    ConnectionStoreSnapshot LoadSnapshot();

    /// <summary>Replaces the list only if it still matches the loaded version; returns the new version.</summary>
    /// <exception cref="ConnectionStoreConflictException">The list changed; nothing was written.</exception>
    string Save(IReadOnlyList<RpadConnection> connections, string? expectedVersion);
}

/// <summary>The list as <c>connections.json</c> in Invoklet's data folder, readable only by its owner
/// on macOS and Linux (a connection string may still hold a password typed into it).</summary>
public sealed class FileConnectionStore : IConnectionStore
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public FileConnectionStore(string? filePath = null) => FilePath = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Invoklet", "connections.json");

    public string FilePath { get; }

    public IReadOnlyList<RpadConnection> Load() => LoadSnapshot().Connections;

    public ConnectionStoreSnapshot LoadSnapshot()
    {
        var bytes = ReadBytes(FilePath);
        if (bytes is null) return new ConnectionStoreSnapshot([], null);
        StoreFile file;
        try
        {
            file = JsonSerializer.Deserialize<StoreFile>(bytes)
                ?? throw new InvalidDataException("The connection list is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The connection list file is damaged.", ex);
        }
        if (file.Version > FormatVersion)
            throw new InvalidDataException($"The connection list was written by a newer Invoklet (format {file.Version}).");
        if (file.Version != FormatVersion)
            throw new InvalidDataException("The connection list file is damaged.");
        var connections = new List<RpadConnection>();
        foreach (var connection in file.Connections ?? [])
        {
            if (!IsWhole(connection)) throw new InvalidDataException("The connection list file is damaged.");
            connections.Add(connection);
        }
        return new ConnectionStoreSnapshot(connections, VersionOf(bytes));
    }

    public string Save(IReadOnlyList<RpadConnection> connections, string? expectedVersion)
    {
        var fullPath = Path.GetFullPath(FilePath);
        var folder = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(folder);
        // Retain the lock inode so all Invoklet writers coordinate on the same file.
        var lockOptions = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var writer = new FileStream(fullPath + ".lock", lockOptions);
        var current = ReadBytes(fullPath);
        if (!string.Equals(expectedVersion, current is null ? null : VersionOf(current), StringComparison.Ordinal))
            throw new ConnectionStoreConflictException();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoreFile { Version = FormatVersion, Connections = [.. connections] }, WriteOptions);
        // Written next to the list and moved over it, so a failed write never leaves half a list.
        var temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, fullPath, overwrite: true);
            return VersionOf(bytes);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // JSON nulls deserialize into properties the rest of Invoklet reads as never null.
    private static bool IsWhole([NotNullWhen(true)] RpadConnection? connection) =>
        connection is { Name: not null, Driver: not null, Params: not null }
        && connection.Params.Values.All(IsWhole)
        && (connection.Extra?.Values.All(IsWhole) ?? true);

    private static bool IsWhole(RpadValue? value) =>
        value is not null && (value.Items?.All(IsWhole) ?? true) && (value.Fields?.Values.All(IsWhole) ?? true);

    private static string VersionOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[]? ReadBytes(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private sealed class StoreFile
    {
        public int Version { get; set; }
        public List<RpadConnection?>? Connections { get; set; }
    }
}
