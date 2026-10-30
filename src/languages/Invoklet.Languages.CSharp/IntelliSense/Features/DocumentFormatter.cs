using System.Text;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.CSharp.IntelliSense.Roslyn;
using Invoklet.Languages.CSharp.IntelliSense.ScriptDocument;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

    /// <summary>Formats the lines that the range touches: from the start of the first to the end of the last.</summary>
    internal Task<IReadOnlyList<TextEdit>?> FormatRangeAsync(string code, int start, int length, CancellationToken ct = default)
    {
        var from = Math.Clamp(start, 0, code.Length);
        var to = Math.Clamp(start + Math.Max(length, 0), from, code.Length);
        return FormatSpanAsync(code, _ => WholeLines(code, from, to), "Format selection", ct);
    }

    /// <summary>After a ';' the statement is formatted, after a '}' the construct the block belongs to, with its header.</summary>
    internal Task<IReadOnlyList<TextEdit>?> FormatOnTypeAsync(string code, int caret, char typed, CancellationToken ct = default)
    {
        if (typed is not (';' or '}') || caret < 1 || caret > code.Length || code[caret - 1] != typed)
            return Task.FromResult<IReadOnlyList<TextEdit>?>(null);
        return FormatSpanAsync(code, root => SpanTypedAt(root, caret, typed), "Format on type", ct);
    }

    // The statement of a ';', or the construct a '}' closes (an 'if' with its blocks, a method, a type): null when the character typed is
    // not that token (it is inside a string or a comment) or has no such construct around it.
    private static TextSpan? SpanTypedAt(SyntaxNode root, int caret, char typed)
    {
        var token = root.FindToken(caret - 1);
        if (token.SpanStart != caret - 1 || token.Span.End != caret) return null;
        var kind = typed == ';' ? SyntaxKind.SemicolonToken : SyntaxKind.CloseBraceToken;
        if (!token.IsKind(kind)) return null;

        SyntaxNode? node = typed == ';'
            ? token.Parent?.AncestorsAndSelf().FirstOrDefault(n => n is StatementSyntax or MemberDeclarationSyntax or UsingDirectiveSyntax)
            : token.Parent is BlockSyntax block && block.Parent is StatementSyntax or MemberDeclarationSyntax or AccessorDeclarationSyntax
                ? block.Parent
                : token.Parent;
        // A statement inside a block is formatted alone; a block's owner is formatted whole, and never more than the one construct.
        return node?.Span;
    }

    private static TextSpan WholeLines(string code, int from, int to)
    {
        var lineStart = from;
        while (lineStart > 0 && code[lineStart - 1] != '\n') lineStart--;
        var lineEnd = to;
        while (lineEnd < code.Length && code[lineEnd] != '\n' && code[lineEnd] != '\r') lineEnd++;
        return TextSpan.FromBounds(lineStart, lineEnd);
    }

    private async Task<IReadOnlyList<TextEdit>?> FormatSpanAsync(
        string code, Func<SyntaxNode, TextSpan?> spanOf, string what, CancellationToken ct)
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
                projectId, VersionStamp.Create(), name: "Format", assemblyName: "Format", language: LanguageNames.CSharp, parseOptions: parseOptions));
            var document = formatWorkspace.AddDocument(projectId, "Script.cs", SourceText.From(code, Encoding.UTF8));

            var root = await document.GetSyntaxRootAsync(ct);
            if (root is null || spanOf(root) is not { } span) return null;

            var formatted = await Formatter.FormatAsync(document, span, formattingOptions, ct);
            var changes = await formatted.GetTextChangesAsync(document, ct);
            return [.. changes.OrderBy(c => c.Span.Start).Select(c => new TextEdit(c.Span.Start, c.Span.Length, c.NewText ?? ""))];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "{What} failed", what); return null; }
    }
}
