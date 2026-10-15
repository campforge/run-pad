using System.Reflection;
using RunPad.Format.RPad;

namespace RunPad.Services.Examples;

/// <summary>A sample script shipped inside the application (Examples/*.rpad, embedded resources).</summary>
/// <param name="Id">Its resource file name, e.g. "quick-start.rpad" — one open tab per example.</param>
/// <param name="Title">The document's first Markdown heading, else a name made from the file name.</param>
/// <param name="Text">The whole .rpad document, front matter and Markdown included.</param>
public sealed record ExampleScript(string Id, string Title, string Text, RpadLanguage Language, RpadKind Kind);

/// <summary>
/// The examples of the Examples panel. They are the user guide's sample scripts, bundled with the
/// application (no download, nothing read from disk) and only ever opened as untitled copies, so the
/// originals cannot change. A missing or unreadable resource leaves it out.
/// </summary>
public static class ExampleScripts
{
    private const string ResourcePrefix = "RunPad.Examples.";

    // The panel's order: from a first script to the debugger.
    private static readonly string[] Order =
    [
        "quick-start.rpad", "expression-kind.rpad", "program-kind.rpad", "products-overview.rpad",
        "live-output.rpad", "debugging-loop.rpad",
    ];

    private static readonly Lazy<IReadOnlyList<ExampleScript>> Loaded = new(() => Load(typeof(ExampleScripts).Assembly));

    public static IReadOnlyList<ExampleScript> All => Loaded.Value;

    internal static IReadOnlyList<ExampleScript> Load(Assembly assembly)
    {
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".rpad", StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..])
            .OrderBy(n => Array.IndexOf(Order, n) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(n => n, StringComparer.Ordinal);

        var result = new List<ExampleScript>();
        foreach (var id in names)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(ResourcePrefix + id);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                if (RpadParser.TryParse(text) is not { } document) continue; // not a valid script: left out
                result.Add(new ExampleScript(id, TitleOf(text, id), text,
                    document.FrontMatter.Language, document.FrontMatter.Kind));
            }
            catch (IOException)
            {
                // unreadable: left out
            }
        }
        return result;
    }

    // "# Quick start" → "Quick start"; without a heading, "quick-start.rpad" → "Quick start".
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
