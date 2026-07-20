using System.Composition.Hosting;
using System.Reflection;
using System.Text.RegularExpressions;
using DotPad.Contracts.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ContractSeverity = DotPad.Contracts.IntelliSense.DiagnosticSeverity;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

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
    private readonly IReadOnlyList<CodeRefactoringProvider> _refactoringProviders;
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

        // Same MEF composition (DotPadRoslynHost already pulls in Microsoft.CodeAnalysis(.CSharp).
        // Features, where both CodeFixProvider AND CodeRefactoringProvider parts live) — same
        // [ExportCodeRefactoringProvider]-attribute language filter, same reasoning.
        var refactoringProviders = new List<CodeRefactoringProvider>();
        foreach (var provider in compositionHost.GetExports<CodeRefactoringProvider>())
        {
            var languages = provider.GetType().GetCustomAttribute<ExportCodeRefactoringProviderAttribute>()?.Languages;
            if (languages != null && languages.Contains(LanguageNames.CSharp))
                refactoringProviders.Add(provider);
        }
        _refactoringProviders = refactoringProviders;
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

    // TODO.md #74 "CodeRefactoringProvider хостинг" — the position-based counterpart to
    // ComputeFixesAsync above: no diagnostic to key off, no FixableDiagnosticIds to pre-filter by, so
    // every registered provider gets a chance at the given span every call (same "a couple hundred
    // providers, once per tab, already realized in the constructor" cost model as code fixes —
    // ComputeRefactoringsAsync itself only iterates the list, doesn't re-discover it). Reuses Flatten/
    // TryMaterializeAsync verbatim: a CodeRefactoringContext's registered CodeAction materializes into
    // NewCode/UsingsToAdd through the exact same preamble-relocation logic a CodeFixContext's does —
    // RegisterRefactoring vs RegisterCodeFix is just a different registration callback name for the
    // same underlying CodeAction type.
    public async Task<IReadOnlyList<ICodeFixInfo>> ComputeRefactoringsAsync(
        Document document, TextSpan span, string userCode, int preambleLength, int userLine, CancellationToken ct)
    {
        var result = new List<ICodeFixInfo>();
        if (userLine < 1) return result;

        foreach (var provider in _refactoringProviders)
        {
            ct.ThrowIfCancellationRequested();

            var actions = new List<CodeAction>();
            var context = new CodeRefactoringContext(document, span, actions.Add, ct);

            try { await provider.ComputeRefactoringsAsync(context); }
            catch (OperationCanceledException) { throw; } // routine — superseded by the next keystroke/caret move
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Refactoring provider {Provider} threw at [{Start}..{End})",
                    provider.GetType().Name, span.Start, span.End);
                continue;
            }

            foreach (var action in actions)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var leaf in Flatten(action))
                {
                    var materialized = await TryMaterializeAsync(leaf, document, preambleLength, userCode.Length, ct);
                    if (materialized is null) continue;
                    var (newCode, usingsToAdd) = materialized.Value;
                    if (newCode == userCode && usingsToAdd.Count == 0) continue;

                    // No underlying diagnostic (there isn't one) to derive a severity from — same
                    // neutral Info tier CollectFixesAsync's own non-diagnostic-adjacent fixes get.
                    result.Add(new RoslynCodeFix(userLine, leaf.Title, newCode, usingsToAdd, ContractSeverity.Info));
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
        catch (OperationCanceledException) { throw; } // routine — superseded by the next keystroke
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
                var materialized = await TryMaterializeAsync(leaf, document, preambleLength, userCode.Length, ct);
                if (materialized is null) continue;
                var (newCode, usingsToAdd) = materialized.Value;
                if (newCode == userCode && usingsToAdd.Count == 0)
                {
                    _logger.LogDebug("Discarding fix {Title} for {Id}: materialized to a no-op (NewCode == userCode, no usings)",
                        leaf.Title, diagnostic.Id);
                    continue;
                }
                result.Add(new RoslynCodeFix(userLine, leaf.Title, newCode, usingsToAdd, ToContractSeverity(diagnostic.Severity)));
            }
        }
    }

    // Some fixes group several candidates under one parent (e.g. multiple "using X;" candidates for
    // an ambiguous/unresolved type) — only the leaves carry real operations to apply.
    private static IEnumerable<CodeAction> Flatten(CodeAction action) =>
        action.NestedActions.Length == 0 ? [action] : action.NestedActions.SelectMany(Flatten);

    // No longer static — needs _logger. Used to be a bare `catch { return null; }` with zero
    // logging, unlike CollectFixesAsync's own catch around RegisterCodeFixesAsync right above (which
    // does log at Debug) — found only by chasing a Windows-CI-only "CodeFixesChanged fires with an
    // empty list, no log output at all" symptom (NotifyTextChanged_Should_MarkUnusedVariableFix_
    // AsWarningSeverity) back through every catch block in this pipeline. That alone wasn't enough
    // though — a first round of this same logging still showed zero messages on the next CI failure,
    // meaning the real culprit isn't an exception here at all: ApplyRelocatingPreambleEdits itself has
    // two `return null;` paths that never throw (see its own doc comment) — a discard-by-design that
    // looks IDENTICAL to "no fix existed" from the outside. Logs the raw TextChanges unconditionally
    // now, before anything can discard them, since that's the one piece of data neither round of
    // logging so far has captured.
    private async Task<(string NewCode, IReadOnlyList<string> UsingsToAdd)?> TryMaterializeAsync(
        CodeAction action, Document originalDocument, int preambleLength, int userCodeLength, CancellationToken ct)
    {
        try
        {
            var operations = await action.GetOperationsAsync(ct);
            var applyOp = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
            if (applyOp is null)
            {
                _logger.LogDebug("Fix {Title}: no ApplyChangesOperation among {Count} operation(s)",
                    action.Title, operations.Length);
                return null;
            }

            var newDocument = applyOp.ChangedSolution.GetDocument(originalDocument.Id);
            if (newDocument is null)
            {
                _logger.LogDebug("Fix {Title}: ApplyChangesOperation's solution has no document for {DocId}",
                    action.Title, originalDocument.Id);
                return null;
            }

            // Some fix providers reformat whitespace they merely touch (e.g. RemoveUnusedVariable
            // normalizing the blank line before the statement it deletes) using Roslyn's own line-ending
            // default, which is "\r\n" on Windows independent of the workspace-level FormattingOptions.
            // NewLine override in CSharpIntelliSenseService's ctor — that override doesn't reliably reach
            // every fix provider's internal Formatter.Format call. This synthetic document only ever
            // contains literal '\n', so a stray "\r\n" inflates the diff against originalDocument by one
            // byte per touched newline, which can shift a change's span just enough to straddle
            // preambleLength below and get the whole fix discarded even though the real edit never
            // touches the preamble. Normalizing back to '\n' before diffing keeps the diff to the actual
            // semantic edit, independent of OS.
            var newDocumentText = (await newDocument.GetTextAsync(ct)).ToString();
            var normalizedText = newDocumentText.Replace("\r\n", "\n");
            if (normalizedText != newDocumentText)
                newDocument = newDocument.WithText(SourceText.From(normalizedText));

            var changes = (await newDocument.GetTextChangesAsync(originalDocument, ct)).ToList();
            _logger.LogDebug("Fix {Title}: {Count} TextChange(s): {Changes}", action.Title, changes.Count,
                string.Join(" | ", changes.Select(c => $"[{c.Span.Start}..{c.Span.End}) -> \"{Truncate(c.NewText)}\"")));

            return ApplyRelocatingPreambleEdits(
                changes, await originalDocument.GetTextAsync(ct), preambleLength, userCodeLength);
        }
        catch (OperationCanceledException) { throw; } // routine — superseded by the next keystroke
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Materializing fix {Title} failed", action.Title);
            return null;
        }
    }

    private static string Truncate(string? text)
    {
        text = (text ?? "").Replace("\r", "\\r").Replace("\n", "\\n");
        return text.Length > 40 ? text[..40] + "…" : text;
    }

    private static readonly Regex _usingInsertionRegex = new(@"^\s*using\s+([\w.]+)\s*;\s*$", RegexOptions.Compiled);

    // Splits a set of TextChanges expressed against the synthetic `_preamble + code` document into
    // "usings to add" (lifted out of the preamble) and "the rest" (shifted back onto the user's
    // code). See the class doc comment for why this can't be a plain substring cut.
    //
    // TODO.md #77: the "rest" used to be reconstructed as everything from preambleLength to
    // originalFullText.Length — correct only as long as nothing follows the user's own code in the
    // synced document. CSharpIntelliSenseService now appends a one-character TrailingPad after it
    // (so a query position sitting at the very end of the user's code no longer coincides with the
    // document's true end — see HeaderLength's doc comment for the underlying Roslyn quirk this
    // works around), so the end of "the user's code" has to be the caller-supplied, actually-known
    // userCodeLength instead of however long the live document happens to be. A fix that removes/
    // replaces the LAST token of the user's code (e.g. "remove unused variable" on the final
    // statement) removes that token's full span including trailing trivia — which now extends into
    // TrailingPad, since a synthetic space right after the last real token attaches to it as trailing
    // trivia the same as any other trailing whitespace. Each change's span is clamped to
    // preambleLength + userCodeLength for exactly this reason: the clamped-off tail is guaranteed to
    // be nothing but that synthetic pad (never meaningful user content), so silently dropping it from
    // what gets deleted/replaced is safe — NewText itself is untouched, only how much of the
    // (padding-only) tail end of the input span gets consumed is shortened.
    private (string NewCode, IReadOnlyList<string> UsingsToAdd)? ApplyRelocatingPreambleEdits(
        List<TextChange> changes, SourceText originalFullText, int preambleLength, int userCodeLength)
    {
        var usingsToAdd = new List<string>();
        var userCodeChanges = new List<TextChange>();
        var userCodeEnd = preambleLength + userCodeLength;

        foreach (var change in changes.OrderBy(c => c.Span.Start))
        {
            if (change.Span.Start >= preambleLength)
            {
                var clampedLength = Math.Max(0, Math.Min(change.Span.End, userCodeEnd) - change.Span.Start);
                userCodeChanges.Add(new TextChange(
                    new TextSpan(change.Span.Start - preambleLength, clampedLength), change.NewText ?? ""));
                continue;
            }

            // Touches the hidden preamble. Only a pure `using X;` insertion right there is
            // recoverable — anything else (replaces/removes existing preamble text, straddles into
            // the user's code, or inserts something that isn't a plain using directive) can't be
            // represented without risking corruption, so bail out entirely.
            if (change.Span.Length > 0 || change.Span.End > preambleLength)
            {
                _logger.LogDebug(
                    "Discarding fix: preamble-touching change [{Start}..{End}) (len={Len}) isn't a pure "
                    + "zero-length insertion at/before preambleLength={PreambleLength}",
                    change.Span.Start, change.Span.End, change.Span.Length, preambleLength);
                return null;
            }

            var match = _usingInsertionRegex.Match(change.NewText ?? "");
            if (!match.Success)
            {
                _logger.LogDebug("Discarding fix: preamble insertion \"{NewText}\" isn't a pure using directive",
                    Truncate(change.NewText));
                return null;
            }
            usingsToAdd.Add(match.Groups[1].Value);
        }

        var userText = originalFullText.GetSubText(new TextSpan(preambleLength, userCodeLength));
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

    // Same mapping as CSharpIntelliSenseService's diagnostics pipeline (Hidden has no ICodeFixInfo
    // equivalent to fall back to — a fix is never offered for a Hidden-severity diagnostic in the
    // first place — so it folds into Info rather than needing a fourth case here).
    private static ContractSeverity ToContractSeverity(RoslynSeverity severity) => severity switch
    {
        RoslynSeverity.Error => ContractSeverity.Error,
        RoslynSeverity.Warning => ContractSeverity.Warning,
        _ => ContractSeverity.Info,
    };

    private sealed record RoslynCodeFix(
        int Line, string Title, string NewCode, IReadOnlyList<string> UsingsToAdd, ContractSeverity Severity) : ICodeFixInfo;
}
