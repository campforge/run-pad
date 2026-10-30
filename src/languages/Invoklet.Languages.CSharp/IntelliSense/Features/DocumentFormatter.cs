using System.Text;
using Invoklet.Languages.CSharp.IntelliSense.Roslyn;
using Invoklet.Languages.CSharp.IntelliSense.ScriptDocument;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace Invoklet.Languages.CSharp.IntelliSense.Features;

/// <summary>Formats the code in a workspace of its own, apart from the analysis document, with the editor's indentation.</summary>
internal sealed class DocumentFormatter(ScriptDocumentSession session, ScriptDocumentLayout layout, ILogger logger)
{
    private readonly ScriptDocumentSession _session = session;
    private readonly ScriptDocumentLayout _layout = layout;
    private readonly ILogger _logger = logger;

    // Formats in a throwaway workspace/project containing only the user's code (no preamble) so
    // Roslyn's Formatter output maps back to the editor text directly, with no preamble/code
    // boundary whitespace to reconcile.
    internal async Task<string?> FormatDocumentAsync(string code, CancellationToken ct = default)
    {
        try
        {
            CSharpParseOptions parseOptions;
            OptionSet formattingOptions;
            lock (_session.SyncLock)
            {
                parseOptions = _layout.ParseOptions;
                formattingOptions = _session.FormattingOptionsSnapshot();
            }
            using var formatWorkspace = new AdhocWorkspace(InvokletRoslynHost.HostServices);
            var projectId = ProjectId.CreateNewId("Format");
            formatWorkspace.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                name: "Format",
                assemblyName: "Format",
                language: LanguageNames.CSharp,
                parseOptions: parseOptions));

            var document = formatWorkspace.AddDocument(projectId, "Script.cs", SourceText.From(code, Encoding.UTF8));
            var formatted = await Formatter.FormatAsync(document, formattingOptions, cancellationToken: ct);
            var text = await formatted.GetTextAsync(ct);
            return text.ToString();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Format failed"); return null; }
    }
}
