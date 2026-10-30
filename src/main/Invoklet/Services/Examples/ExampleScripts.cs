using System.Reflection;
using Invoklet.Format.MdRun;

namespace Invoklet.Services.Examples;

/// <summary>A sample script shipped inside the application (Examples/**/*.inv, embedded resources).</summary>
/// <param name="Id">Its file name, e.g. "quick-start.inv" — one open tab per example; unique across folders.</param>
/// <param name="Title">The document's first Markdown heading, else a name made from the file name.</param>
/// <param name="Text">The whole .inv document, front matter and Markdown included.</param>
/// <param name="Folder">Its folder under Examples, e.g. "getting-started"; empty at the top.</param>
public sealed record ExampleScript(string Id, string Title, string Text, MdRunLanguage Language, MdRunKind Kind, string Folder = "")
{
    /// <summary>The folder's name as the Samples tree shows it: "getting-started" → "Getting started".</summary>
    public static string FolderTitle(string folder)
    {
        var name = folder.Replace('-', ' ');
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }
}

/// <summary>
/// The examples of the Examples panel. They are the user guide's sample scripts, bundled with the
/// application (no download, nothing read from disk) and only ever opened as untitled copies, so the
/// originals cannot change. A missing or unreadable resource leaves it out.
/// </summary>
public static class ExampleScripts
{
    private const string ResourcePrefix = "Invoklet.Examples.";

    // The Samples order, folders included: from a first script to the debugger. Anything not listed
    // follows, by path.
    private static readonly string[] Order =
    [
        "getting-started/quick-start.inv", "getting-started/expression-kind.inv", "getting-started/program-kind.inv",
        "output/products-overview.inv", "output/live-output.inv",
        "debugging/debugging-loop.inv",
    ];

    private static readonly Lazy<IReadOnlyList<ExampleScript>> Loaded = new(() => Load(typeof(ExampleScripts).Assembly));

    public static IReadOnlyList<ExampleScript> All => Loaded.Value;

    internal static IReadOnlyList<ExampleScript> Load(Assembly assembly)
    {
        // The build keeps a subfolder in the name with the platform's separator, '/' or a backslash.
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".inv", StringComparison.Ordinal))
            .Select(n => (Resource: n, Path: n[ResourcePrefix.Length..].Replace('\\', '/')))
            .OrderBy(n => Array.IndexOf(Order, n.Path) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(n => n.Path, StringComparer.Ordinal);

        var result = new List<ExampleScript>();
        foreach (var (resource, path) in names)
        {
            var slash = path.LastIndexOf('/');
            var id = path[(slash + 1)..];
            var folder = slash < 0 ? "" : path[..slash];
            if (result.Any(e => e.Id == id)) continue; // ids identify tabs: the first of a name wins
            try
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                if (MdRunParser.TryParse(text) is not { } document) continue; // not a valid script: left out
                result.Add(new ExampleScript(id, TitleOf(text, id), text,
                    document.FrontMatter.Language, document.FrontMatter.Kind, folder));
            }
            catch (IOException)
            {
                // unreadable: left out
            }
        }
        return result;
    }

    // "# Quick start" → "Quick start"; without a heading, "quick-start.inv" → "Quick start".
    internal static string TitleOf(string text, string id)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("# ", StringComparison.Ordinal) && trimmed.Length > 2)
                return trimmed[2..].Trim();
        }
        var name = Path.GetFileNameWithoutExtension(id).Replace('-', ' ');
        return name.Length == 0 ? id : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
