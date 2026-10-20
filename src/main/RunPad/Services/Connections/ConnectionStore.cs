using System.Text.Json;
using RunPad.Format.RPad;

namespace RunPad.Services.Connections;

/// <summary>
/// The user's own connections (Tools → Connections), kept by RunPad outside every script, in the
/// same shape as a script's <c>connections:</c>. Secrets are not in it: their values stay in the
/// Secret Store and connections name them with <c>${secret:…}</c>.
/// </summary>
public interface IConnectionStore
{
    /// <summary>The store's file.</summary>
    string FilePath { get; }

    /// <summary>Every connection, in the saved order; empty when there is no list yet.</summary>
    /// <exception cref="InvalidDataException">The file is damaged or from a newer RunPad; it is left as it is.</exception>
    IReadOnlyList<RpadConnection> Load();

    /// <summary>Replaces the whole list.</summary>
    void Save(IReadOnlyList<RpadConnection> connections);
}

/// <summary>The list as <c>connections.json</c> in RunPad's data folder, readable only by its owner
/// on macOS and Linux (a connection string may still hold a password typed into it).</summary>
public sealed class FileConnectionStore : IConnectionStore
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public FileConnectionStore(string? filePath = null) => FilePath = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RunPad", "connections.json");

    public string FilePath { get; }

    public IReadOnlyList<RpadConnection> Load()
    {
        if (!File.Exists(FilePath)) return [];
        StoreFile file;
        try
        {
            file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllBytes(FilePath))
                ?? throw new InvalidDataException("The connection list is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The connection list file is damaged.", ex);
        }
        if (file.Version != FormatVersion)
            throw new InvalidDataException($"The connection list was written by a newer RunPad (format {file.Version}).");
        return file.Connections ?? [];
    }

    public void Save(IReadOnlyList<RpadConnection> connections)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(folder);
        // Written next to the list and moved over it, so a failed write never leaves half a list.
        var temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(stream, new StoreFile { Version = FormatVersion, Connections = [.. connections] }, WriteOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private sealed class StoreFile
    {
        public int Version { get; set; }
        public List<RpadConnection>? Connections { get; set; }
    }
}
