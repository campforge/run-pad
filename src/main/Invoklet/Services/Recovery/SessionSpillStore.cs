using System.Text.Json;
using Invoklet.Services.Tabs;
using Invoklet.UserControls.Models;

namespace Invoklet.Services.Recovery;

/// <summary>
/// Keeps the released content of tabs in the <c>spill</c> folder of this instance's recovery session. The
/// session folder is private to the user and is locked by the instance that owns it, which is also what
/// tells <see cref="DiskRecoveryStore"/> that the spill of a session nobody owns is stale and can go. Before
/// the recovery store has opened its session there is nowhere to keep anything: nothing is released.
/// </summary>
public sealed class SessionSpillStore(Func<string?> sessionFolder) : ITabSpillStore
{
    internal const string FolderName = "spill";

    public bool TryWrite(Guid tab, ReleasedContent content)
    {
        if (Folder() is not { } folder) return false;
        try
        {
            Directory.CreateDirectory(folder);
            DiskRecoveryStore.RestrictDirectory(folder);
            var path = PathOf(folder, tab);
            var temp = path + ".tmp";
            Write(temp, JsonSerializer.SerializeToUtf8Bytes(content));
            File.Move(temp, path, overwrite: true);
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
            return JsonSerializer.Deserialize<ReleasedContent>(File.ReadAllBytes(PathOf(folder, tab)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Delete(Guid tab)
    {
        if (Folder() is not { } folder) return;
        try
        {
            File.Delete(PathOf(folder, tab));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover file goes with the folder.
        }
    }

    public void DeleteAll()
    {
        if (Folder() is { } folder) DiskRecoveryStore.TryDeleteDirectory(folder);
    }

    private string? Folder() => sessionFolder() is { } session ? Path.Combine(session, FolderName) : null;

    private static string PathOf(string folder, Guid tab) => Path.Combine(folder, tab.ToString("N") + ".json");

    // Not fsynced: unlike a checkpoint this is only a cache of what the session holds.
    private static void Write(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        stream.Write(bytes);
    }
}
