using System.Composition.Hosting;
using System.Reflection;
using System.Text.RegularExpressions;
using DotPad.Contracts.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace DotPad.Languages.CSharp.IntelliSense;

// Real Roslyn quick fixes for the lightbulb margin (ReSharper/LINQPad-style Code Actions), instead
// of the hand-rolled "add using" search this replaces. Discovers every CodeFixProvider registered
// as a MEF part in Microsoft.CodeAnalysis(.CSharp).Features (Add Import, Implement Interface,
// Implement Abstract Class, Generate Variable/Method, Fully Qualify, ...) via the same
// System.Composition container DotPadRoslynHost builds for the AdhocWorkspace — the same mechanism
// RoslynPad/OmniSharp use — rather than the full VS CodeFixService, which needs several workspace
// services (diagnostic analyzer service, solution crawler, ...) this host never sets up.
//
// The tricky part is that CSharpIntelliSenseService's Roslyn document is not the user's literal
// script text: it's `_preamble + code` (a block of default `using` directives prepended so
// completion/diagnostics work without the user typing them). A fix provider operating on that
// synthetic document may insert a new `using` directive among the preamble's own — i.e. entirely
// *before* the offset we'd naively slice at to recover "the user's code" — so it can't be
// recovered by just cutting the new full text at the old preamble length. Text changes are instead
// split into "touches the preamble" (a pure `using X;` insertion there is lifted out into
// ICodeFixInfo.UsingsToAdd, per docs/ImplementationPlanRPad.md — usings are project-level metadata,
// not text in the visible script) and "touches the user's code" (shifted back by -preambleLength and
// applied directly to produce NewCode). Any preamble edit that isn't a recognizable pure `using`
// insertion (replaces/deletes preamble text, or something else entirely) is treated as unsafe to
// reconstruct and the whole fix is discarded.
internal sealed class RoslynCodeFixEngine
{
    private readonly IReadOnlyList<CodeFixProvider> _providers;
    private readonly ILogger _logger;

    public RoslynCodeFixEngine(CompositionHost compositionHost, ILogger logger)
    {
        _logger = logger;

        // Roslyn's own MEF metadata view used to filter exports by language is internal to
        // Microsoft.CodeAnalysis.Features, so language filtering instead reads the same
        // [ExportCodeFixProvider] attribute directly off each concrete type. This realizes every
        // provider once, up front (a couple hundred, one-time cost per tab).
        var providers = new List<CodeFixProvider>();
        foreach (var provider in compositionHost.GetExports<CodeFixProvider>())
        {
            var languages = provider.GetType().GetCustomAttribute<ExportCodeFixProviderAttribute>()?.Languages;
            if (languages != null && languages.Contains(LanguageNames.CSharp))
                providers.Add(provider);
        }
        _providers = providers;
    }

    public async Task<IReadOnlyList<ICodeFixInfo>> ComputeFixesAsync(
        Document document, IReadOnlyList<Diagnostic> diagnostics, string userCode, int preambleLength, CancellationToken ct)
    {
        var result = new List<ICodeFixInfo>();

        foreach (var group in diagnostics.Where(d => d.Location.IsInSource).GroupBy(d => d.Id))
        {
            var providers = _providers.Where(p => p.FixableDiagnosticIds.Contains(group.Key)).ToList();
            if (providers.Count == 0) continue;

            foreach (var diagnostic in group)
            {
                ct.ThrowIfCancellationRequested();

                var userLine = GetLineNumber(userCode, diagnostic.Location.SourceSpan.Start - preambleLength);
                if (userLine < 1) continue;

                foreach (var provider in providers)
                {
                    await CollectFixesAsync(provider, document, diagnostic, userCode, preambleLength, userLine, result, ct);
                }
            }
        }

        return result;
    }

    private async Task CollectFixesAsync(
        CodeFixProvider provider,
        Document document, Diagnostic diagnostic, string userCode, int preambleLength, int userLine,
        List<ICodeFixInfo> result, CancellationToken ct)
    {
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), ct);

        try { await provider.RegisterCodeFixesAsync(context); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Code fix provider {Provider} threw for {Id}", provider.GetType().Name, diagnostic.Id);
            return;
        }

        foreach (var action in actions)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var leaf in Flatten(action))
            {
                var materialized = await TryMaterializeAsync(leaf, document, preambleLength, ct);
                if (materialized is null) continue;
                var (newCode, usingsToAdd) = materialized.Value;
                if (newCode == userCode && usingsToAdd.Count == 0) continue;
                result.Add(new RoslynCodeFix(userLine, leaf.Title, newCode, usingsToAdd));
            }
        }
    }

    // Some fixes group several candidates under one parent (e.g. multiple "using X;" candidates for
    // an ambiguous/unresolved type) — only the leaves carry real operations to apply.
    private static IEnumerable<CodeAction> Flatten(CodeAction action) =>
        action.NestedActions.Length == 0 ? [action] : action.NestedActions.SelectMany(Flatten);

    private static async Task<(string NewCode, IReadOnlyList<string> UsingsToAdd)?> TryMaterializeAsync(
        CodeAction action, Document originalDocument, int preambleLength, CancellationToken ct)
    {
        try
        {
            var operations = await action.GetOperationsAsync(ct);
            var applyOp = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
            if (applyOp is null) return null;

            var newDocument = applyOp.ChangedSolution.GetDocument(originalDocument.Id);
            if (newDocument is null) return null;

            var changes = (await newDocument.GetTextChangesAsync(originalDocument, ct)).ToList();
            return ApplyRelocatingPreambleEdits(changes, await originalDocument.GetTextAsync(ct), preambleLength);
        }
        catch { return null; }
    }

    private static readonly Regex _usingInsertionRegex = new(@"^\s*using\s+([\w.]+)\s*;\s*$", RegexOptions.Compiled);

    // Splits a set of TextChanges expressed against the synthetic `_preamble + code` document into
    // "usings to add" (lifted out of the preamble) and "the rest" (shifted back onto the user's
    // code). See the class doc comment for why this can't be a plain substring cut.
    private static (string NewCode, IReadOnlyList<string> UsingsToAdd)? ApplyRelocatingPreambleEdits(
        List<TextChange> changes, SourceText originalFullText, int preambleLength)
    {
        var usingsToAdd = new List<string>();
        var userCodeChanges = new List<TextChange>();

        foreach (var change in changes.OrderBy(c => c.Span.Start))
        {
            if (change.Span.Start >= preambleLength)
            {
                userCodeChanges.Add(new TextChange(
                    new TextSpan(change.Span.Start - preambleLength, change.Span.Length), change.NewText ?? ""));
                continue;
            }

            // Touches the hidden preamble. Only a pure `using X;` insertion right there is
            // recoverable — anything else (replaces/removes existing preamble text, straddles into
            // the user's code, or inserts something that isn't a plain using directive) can't be
            // represented without risking corruption, so bail out entirely.
            if (change.Span.Length > 0 || change.Span.End > preambleLength)
                return null;

            var match = _usingInsertionRegex.Match(change.NewText ?? "");
            if (!match.Success) return null;
            usingsToAdd.Add(match.Groups[1].Value);
        }

        var userText = originalFullText.GetSubText(new TextSpan(preambleLength, originalFullText.Length - preambleLength));
        return (userText.WithChanges(userCodeChanges).ToString(), usingsToAdd);
    }

    private static int GetLineNumber(string code, int offset)
    {
        if (offset < 0 || offset > code.Length) return -1;
        var line = 1;
        for (var i = 0; i < offset && i < code.Length; i++)
            if (code[i] == '\n') line++;
        return line;
    }

    private sealed record RoslynCodeFix(int Line, string Title, string NewCode, IReadOnlyList<string> UsingsToAdd) : ICodeFixInfo;
}
