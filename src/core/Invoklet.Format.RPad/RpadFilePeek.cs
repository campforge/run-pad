namespace Invoklet.Format.RPad;

// A cheap peek at a .rpad file's front-matter `language:` — reads and parses just enough to answer
// "what language is this script", without opening a full editor tab for it. Used by the UI layer
// both to pick an IntelliSense factory for a tab (EditorTabsControl) and to choose a file tree
// node's per-language icon (FileTreeViewModel) — lives here, next to RpadParser/RpadLanguage
// themselves, rather than in the main UI project, since it's fundamentally a .rpad-format concern.
public static class RpadFilePeek
{
    // Best-effort answer for display purposes (file tree icon) — an invalid document reads as C#.
    public static RpadLanguage PeekLanguage(string? filePath)
        => PeekDocumentLanguage(filePath) ?? RpadLanguage.CSharp;

    // Like PeekLanguage, but null when the file exists and isn't a valid .rpad document (see
    // RpadParser.Parse) — such a tab is opened as raw text with Run/Debug blocked, so it
    // shouldn't get language services wired up against its YAML/Markdown either. A missing or
    // unreadable file still answers C#, same as an empty new document.
    public static RpadLanguage? PeekDocumentLanguage(string? filePath)
    {
        if (filePath is null || !File.Exists(filePath)) return RpadLanguage.CSharp;
        try
        {
            var raw = File.ReadAllText(filePath);
            return RpadParser.TryParse(raw)?.FrontMatter.Language;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RpadLanguage.CSharp;
        }
    }
}
