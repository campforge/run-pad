using System.Text.Json;
using Invoklet.Services.Tabs;
using Invoklet.UserControls.Models;

namespace Invoklet.Services.Recovery;

/// <summary>Where this instance's recovery session lives, once it is open.</summary>
public interface ISessionFolder
{
    /// <summary>The folder of the session; null before it is open.</summary>
    string? SessionFolder { get; }
}

/// <summary>The files of the spill; replaced in tests.</summary>
internal interface ISpillFiles
{
    /// <summary>Writes the file, replacing one with that name. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</summary>
    void Write(string folder, string name, byte[] bytes);

    /// <summary>Reads the file. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</summary>
    byte[] Read(string folder, string name);

    void DeleteFile(string folder, string name);

    void DeleteFolder(string folder);
}

/// <summary>Private files under the session folder: the folder gets the mode of the recovery folders, a file is replaced whole.</summary>
internal sealed class DiskSpillFiles : ISpillFiles
{
    public void Write(string folder, string name, byte[] bytes)
    {
        Directory.CreateDirectory(folder);
        DiskRecoveryStore.RestrictDirectory(folder);
        var path = Path.Combine(folder, name);
        var temp = path + ".tmp";
        // Not fsynced: unlike a checkpoint this is only a cache of what the session holds.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options)) stream.Write(bytes);
        File.Move(temp, path, overwrite: true);
    }

    public byte[] Read(string folder, string name) => File.ReadAllBytes(Path.Combine(folder, name));

    public void DeleteFile(string folder, string name)
    {
        try
        {
            File.Delete(Path.Combine(folder, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover file goes with the folder.
        }
    }

    public void DeleteFolder(string folder) => DiskRecoveryStore.TryDeleteDirectory(folder);
}

/// <summary>
/// Keeps the released content of tabs in the <c>spill</c> folder of this instance's recovery session. The
/// session folder is private to the user and is locked by the instance that owns it, which is also what
/// tells <see cref="DiskRecoveryStore"/> that the spill of a session nobody owns is stale and can go. Before
/// the recovery store has opened its session there is nowhere to keep anything: nothing is released.
/// </summary>
public sealed class SessionSpillStore : ITabSpillStore
{
    internal const string FolderName = "spill";

    private readonly ISessionFolder _session;
    private readonly ISpillFiles _files;

    public SessionSpillStore(ISessionFolder session) : this(session, new DiskSpillFiles())
    {
    }

    internal SessionSpillStore(ISessionFolder session, ISpillFiles files)
    {
        _session = session;
        _files = files;
    }

    public bool TryWrite(Guid tab, ReleasedContent content)
    {
        if (Folder() is not { } folder) return false;
        try
        {
            _files.Write(folder, NameOf(tab), JsonSerializer.SerializeToUtf8Bytes(content));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public ReleasedContent? TryRead(Guid tab)
    {
        if (Folder() is not { } folder) return null;
        try
        {
            return JsonSerializer.Deserialize<ReleasedContent>(_files.Read(folder, NameOf(tab)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Delete(Guid tab)
    {
        if (Folder() is { } folder) _files.DeleteFile(folder, NameOf(tab));
    }

    public void DeleteAll()
    {
        if (Folder() is { } folder) _files.DeleteFolder(folder);
    }

    private string? Folder() => _session.SessionFolder is { } session ? Path.Combine(session, FolderName) : null;

    private static string NameOf(Guid tab) => tab.ToString("N") + ".json";
}
