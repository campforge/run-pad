namespace DotPad.Format.RPad;

// A cheap peek at a .rpad file's front-matter `language:` — reads and parses just enough to answer
// "what language is this script", without opening a full editor tab for it. Used by the UI layer
// both to pick an IntelliSense factory for a tab (EditorTabsControl) and to choose a file tree
// node's per-language icon (FileTreeViewModel) — lives here, next to RpadParser/RpadLanguage
// themselves, rather than in the main UI project, since it's fundamentally a .rpad-format concern.
public static class RpadFilePeek
{
    public static RpadLanguage PeekLanguage(string? filePath)
    {
        if (filePath is null || !File.Exists(filePath)) return RpadLanguage.CSharp;
        try
        {
            var raw = File.ReadAllText(filePath);
            return RpadParser.TryParse(raw)?.FrontMatter.Language ?? RpadLanguage.CSharp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RpadLanguage.CSharp;
        }
    }
}
