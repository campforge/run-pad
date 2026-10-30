using System.Text.Json;
using Invoklet.UserControls.Models;

namespace Invoklet.Services.Tabs;

/// <summary>One file per tab in a folder of Invoklet's own data, private to the user and to this process.</summary>
public sealed class TempFileTabSpillStore : ITabSpillStore
{
    private readonly string _directory;

    public TempFileTabSpillStore(string directory) => _directory = directory;

    public static TempFileTabSpillStore ForThisProcess() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Invoklet", "tab-spill",
        Environment.ProcessId.ToString()));

    public bool TryWrite(Guid tab, ReleasedContent content)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = PathOf(tab);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(content));
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
        try
        {
            return JsonSerializer.Deserialize<ReleasedContent>(File.ReadAllBytes(PathOf(tab)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Delete(Guid tab)
    {
        try
        {
            File.Delete(PathOf(tab));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover file is removed with the folder.
        }
    }

    public void DeleteAll()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only temporary data is left behind.
        }
    }

    private string PathOf(Guid tab) => Path.Combine(_directory, tab.ToString("N") + ".json");
}
