using System.Text.Json;
using Invoklet.UserControls.Models;

namespace Invoklet.Services;

/// <summary>
/// The output of a tab's last run, kept in the folder the script is built and run in, next to its library.
/// The folder belongs to the script (it outlives the tab), so the output is back whenever the tab is built
/// again from it: after the tab's content was released, or in a later session.
/// </summary>
internal static class RunOutputFile
{
    private const string FileName = "run-output.json";

    internal static void Save(string outputDirectory, TabOutputState output)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, FileName);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(output));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Null when the folder holds no output, or one that cannot be read.</summary>
    internal static TabOutputState? TryLoad(string outputDirectory)
    {
        try
        {
            return JsonSerializer.Deserialize<TabOutputState>(File.ReadAllBytes(Path.Combine(outputDirectory, FileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
