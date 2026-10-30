using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Invoklet.Contracts.Connections;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp.Compiler;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using NuGet.Configuration;
using ContractSeverity = Invoklet.Contracts.IntelliSense.DiagnosticSeverity;
using ExecutionRequest = Invoklet.Contracts.Models.ExecutionRequest;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Invoklet.Languages.CSharp.IntelliSense;

/// <summary>The refactorings the lightbulb offers at the caret, the edit a chosen fix or refactoring makes to the user's code, and
/// the usings that resolve every missing name at once. A fix made for the program kind is re-indented outside its hidden wrapper.</summary>
internal sealed class RefactoringService(
    ScriptDocumentSession session, Func<string, Document> syncDocument, ScriptDocumentLayout layout,
    RoslynCodeFixEngine codeFixEngine, ILogger logger)
{
    private readonly ScriptDocumentSession _session = session;
    private readonly Func<string, Document> _syncDocument = syncDocument;
    private readonly ScriptDocumentLayout _layout = layout;
    private readonly RoslynCodeFixEngine _codeFixEngine = codeFixEngine;
    private readonly ILogger _logger = logger;

    internal Task<IReadOnlyList<ICodeFixInfo>?> GetRefactoringsAsync(
        string code, int position, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IReadOnlyList<ICodeFixInfo>?)await TryGetRefactoringsAsync(code, position, ct), ct);

    private async Task<IReadOnlyList<ICodeFixInfo>?> TryGetRefactoringsAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            if (position < 0 || position > code.Length) return null;

            var document = _syncDocument(code);
            var headerLength = _layout.HeaderLength(code);
            var span = new TextSpan(_layout.ToDocumentOffset(code, position), 0);

            // 1-based, same convention as CodeFixEntry.Line/RoslynCodeFixEngine's own diagnostic-fix
            // line lookup — LinePosition.Line itself is 0-based.
            var userLine = SourceText.From(code).Lines.GetLinePosition(position).Line + 1;

            return await _codeFixEngine.ComputeRefactoringsAsync(document, span, _layout.Projection(code).Text, headerLength, userLine, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "GetRefactoringsAsync failed"); return null; }
    }

    // The deferred half of
    // both CodeFixesChanged's fixes and GetRefactoringsAsync's refactorings, see IIntelliSenseService.
    // MaterializeFixAsync's own doc comment. No SyncDocument call here — the cached CodeAction
    // RoslynCodeFixEngine looks key up already carries its own document snapshot internally (captured
    // when it was registered), so materializing it needs nothing from the live workspace at all, only
    // the code/preamble metrics needed for the relocating-preamble math.
    internal Task<IMaterializedFix?> MaterializeFixAsync(string code, string key, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IMaterializedFix?)await TryMaterializeFixAsync(code, key, ct), ct);

    private async Task<IMaterializedFix?> TryMaterializeFixAsync(string code, string key, CancellationToken ct)
    {
        try
        {
            var headerLength = _layout.HeaderLength(code);
            var materialized = await _codeFixEngine.MaterializeAsync(key, _layout.Projection(code).Text, headerLength, ct);
            if (materialized is null) return null;
            var projection = _layout.Projection(code);
            if (projection.RestoreEdit(materialized.Value.NewCode, materialized.Value.ChangedSpans,
                    _layout.Prefix(code), _layout.Suffix(code), _layout.ParseOptions) is not var (restored, changedSpans)) return null;
            var newCode = restored;
            if (_layout.Kind == MdRunKind.Program && restored != code)
            {
                // A class compiled outside the hidden one was formatted at its own columns already.
                var moved = projection.HasChanges ? ProgramBodyProjection.Create(restored, _layout.ParseOptions).Moved : [];
                newCode = await ReindentOutsideTheWrapperAsync(restored,
                    [.. changedSpans.Where(span => !moved.Any(m => m.Contains(span)))], ct);
            }
            return new SimpleMaterializedFix(newCode, materialized.Value.UsingsToAdd);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "MaterializeFixAsync failed"); return null; }
    }

    // The Program kind analyses the user's members inside the hidden wrapper class while they sit at
    // column 0 of the editor, so the formatter behind a code action indents what it writes one level
    // deeper than the visible code (braces under an `if`, a member's own line, a wrapped expression).
    // Only the lines the action's own text changes touch are corrected, found from those changes
    // rather than by comparing the whole script, so a large script costs what its text costs.
    //  - A continuation line the action broke off (its line break and indentation are the action's
    //    own text) loses the extra level, unless it starts at a token column of the line above:
    //    then the action aligned it there, and the column is the same in the editor.
    //  - The touched lines are then formatted again where the wrapper and the user's columns agree —
    //    every line one level in, inside a class — and that level is taken off again. Lines aligned
    //    to a token stay aligned; lines the action left alone are not touched.
    internal async Task<string> ReindentOutsideTheWrapperAsync(string newCode, IReadOnlyList<TextSpan> changedSpans, CancellationToken ct)
    {
        if (changedSpans.Count == 0) return newCode;

        OptionSet options;
        lock (_session.SyncLock) options = _session.FormattingOptionsSnapshot();
        var useTabs = options.GetOption(FormattingOptions.UseTabs, LanguageNames.CSharp);
        var unit = useTabs ? "\t" : new string(' ', options.GetOption(FormattingOptions.IndentationSize, LanguageNames.CSharp));
        CSharpParseOptions parseOptions;
        lock (_session.SyncLock) parseOptions = _layout.ParseOptions;

        var lines = newCode.Split('\n');
        var lineStarts = new int[lines.Length];
        for (int i = 0, offset = 0; i < lines.Length; offset += lines[i].Length + 1, i++)
            lineStarts[i] = offset;

        var touched = new SortedSet<int>();
        foreach (var span in changedSpans)
        {
            ct.ThrowIfCancellationRequested();
            var first = LineOf(lineStarts, span.Start);
            var last = LineOf(lineStarts, Math.Max(span.Start, span.End - 1));
            for (var line = first; line <= last; line++)
            {
                touched.Add(line);
                if (!useTabs && line > 0 && lineStarts[line] > span.Start && lineStarts[line] <= span.End)
                    lines[line] = WithoutWrapperLevel(lines[line], lines[line - 1], unit);
            }
        }

        const string header = "class __InvokletIndent\n{\n";
        var text = new StringBuilder(header, header.Length + newCode.Length + lines.Length * unit.Length + 4);
        var spans = new List<TextSpan>(touched.Count);
        for (var i = 0; i < lines.Length; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            var start = text.Length;
            if (lines[i].Length > 0) text.Append(unit);
            text.Append(lines[i]);
            if (touched.Contains(i)) spans.Add(TextSpan.FromBounds(start, text.Length));
            if (i < lines.Length - 1) text.Append('\n');
        }
        text.Append("\n}\n");

        using var formatWorkspace = new AdhocWorkspace(InvokletRoslynHost.HostServices);
        var projectId = ProjectId.CreateNewId("Indent");
        formatWorkspace.AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), "Indent", "Indent",
            LanguageNames.CSharp, parseOptions: parseOptions));
        var document = formatWorkspace.AddDocument(projectId, "Script.cs", SourceText.From(text.ToString(), Encoding.UTF8));
        var formatted = await Formatter.FormatAsync(document, spans, options.WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, "\n"), ct);
        var result = (await formatted.GetTextAsync(ct)).ToString();
        if (!result.StartsWith(header, StringComparison.Ordinal) || !result.EndsWith("\n}\n", StringComparison.Ordinal))
            return newCode;

        var body = result[header.Length..^"\n}\n".Length].Split('\n');
        return string.Join('\n', body.Select(l => l.StartsWith(unit, StringComparison.Ordinal) ? l[unit.Length..] : l));
    }

    private static int LineOf(int[] lineStarts, int position)
    {
        var index = Array.BinarySearch(lineStarts, position);
        return index >= 0 ? index : ~index - 1;
    }

    // One indentation level less for a continuation line of spaces, unless its first character sits
    // under the start of a token of the line above (an alignment).
    private static string WithoutWrapperLevel(string line, string above, string unit)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ') indent++;
        if (indent < unit.Length || indent == line.Length || line[indent] == '\t') return line;
        var alignedToToken = indent < above.Length && !char.IsWhiteSpace(above[indent])
            && (char.IsWhiteSpace(above[indent - 1]) || !char.IsLetterOrDigit(above[indent - 1]) || !char.IsLetterOrDigit(above[indent]));
        return alignedToToken ? line : line[unit.Length..];
    }

    // ReSharper-style "resolve all missing usings" — see IIntelliSenseService's own doc comment for
    // the contract. Only compiler diagnostics (never analyzer diagnostics — see
    // ComputeDiagnosticsAndCodeFixesAsync's own note on why those need a separate GetCompilationAsync
    // call this doesn't need) are relevant: CS0246 ("type or namespace not found") and CS0103 ("name
    // does not exist") are the two Roslyn actually attaches an "Add using X;" fix to.
    internal Task<IMaterializedFix> ResolveAllMissingUsingsAsync(string code, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IMaterializedFix)await TryResolveAllMissingUsingsAsync(code, ct), ct);

    private async Task<IMaterializedFix> TryResolveAllMissingUsingsAsync(string code, CancellationToken ct)
    {
        try
        {
            var document = _syncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model is null) return new SimpleMaterializedFix(code, []);

            var headerLength = _layout.HeaderLength(code);
            var diagnostics = model.GetDiagnostics(cancellationToken: ct)
                .Where(d => d.Id is "CS0246" or "CS0103")
                .ToList();

            var usings = await _codeFixEngine.ResolveAllMissingUsingsAsync(document, diagnostics, _layout.Projection(code).Text, headerLength, ct);
            return new SimpleMaterializedFix(code, usings);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ResolveAllMissingUsingsAsync failed");
            return new SimpleMaterializedFix(code, []);
        }
    }

    private sealed record SimpleMaterializedFix(string NewCode, IReadOnlyList<string> UsingsToAdd) : IMaterializedFix;
}
