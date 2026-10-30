using System.Net;
using System.Text;

namespace Invoklet.Services;

/// <summary>The results of a script as a page that opens in any browser.</summary>
internal static class ResultsHtml
{
    /// <summary>The stylesheet and the output as one document: UTF-8, titled, with the colours of the theme the results were drawn in.</summary>
    public static string Build(string css, string content, string title)
    {
        var page = new StringBuilder();
        page.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>")
            .Append(WebUtility.HtmlEncode(title))
            // A stylesheet that held "</style" would end itself early and let the rest be read as markup.
            .Append("</title>\n<style>\n").Append(css.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase))
            .Append("\n</style>\n</head>\n<body>\n").Append(content).Append("\n</body>\n</html>\n");
        return page.ToString();
    }
}

/// <summary>Saves the results panel of a tab as an HTML file: asks where, writes it, and says so when there is nothing to save or the file cannot be written.</summary>
internal sealed class ResultsExporter(
    IWindowService windows, Func<string> css, Func<string> content, Func<string?> scriptPath, Func<string> startFolder,
    Func<string, string, CancellationToken, Task>? write = null)
{
    private readonly Func<string, string, CancellationToken, Task> _write = write ?? ((path, text, ct) => File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct));

    public async Task ExportAsync(CancellationToken ct = default)
    {
        var html = content();
        if (html.Length == 0)
        {
            await windows.ShowErrorAsync("Export Results", "There are no results to export.");
            return;
        }

        var script = scriptPath();
        var name = script is null ? "Results" : Path.GetFileNameWithoutExtension(script);
        var folder = script is null ? startFolder() : Path.GetDirectoryName(script) ?? startFolder();
        var path = await windows.ShowSaveHtmlFileDialogAsync(name + ".html", folder);
        if (path is null) return;

        try
        {
            await _write(path, ResultsHtml.Build(css(), html, name + " — results"), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await windows.ShowErrorAsync("Export Results Failed", $"Could not write '{path}':\n{ex.Message}");
        }
    }
}
