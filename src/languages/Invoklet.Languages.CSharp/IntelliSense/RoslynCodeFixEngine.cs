using System.Composition.Hosting;
using System.Reflection;
using System.Text.RegularExpressions;
using Invoklet.Contracts.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ContractSeverity = Invoklet.Contracts.IntelliSense.DiagnosticSeverity;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Invoklet.Languages.CSharp.IntelliSense;

// Real Roslyn quick fixes for the lightbulb margin (ReSharper/LINQPad-style Code Actions), instead
// of the hand-rolled "add using" search this replaces. Discovers every CodeFixProvider registered
// as a MEF part in Microsoft.CodeAnalysis(.CSharp).Features (Add Import, Implement Interface,
// Implement Abstract Class, Generate Variable/Method, Fully Qualify, ...) via the same
// System.Composition container InvokletRoslynHost builds for the AdhocWorkspace — the same mechanism
// RoslynPad/OmniSharp use — rather than the full VS CodeFixService, which needs several workspace
// services (diagnostic analyzer service, solution crawler, ...) this host never sets up.
//
// ComputeFixesAsync/ComputeRefactoringsAsync used to materialize
// EVERY candidate (CodeAction.GetOperationsAsync + diffing — the expensive part, see
// TryMaterializeAsync's own comment) from EVERY applicable provider, on EVERY keystroke/caret-move,
// regardless of whether the user would ever click any of them. Measured cost on realistic script
// sizes: ~25-50ms per GetRefactoringsAsync call (every caret move), ~150ms-1.25s of the ~500ms-
// debounced background analysis tick's own budget. Now split in two: listing candidates (cheap —
// RegisterCodeFixesAsync/ComputeRefactoringsAsync on a provider just constructs CodeAction objects,
// doesn't execute anything) versus materializing ONE of them (expensive, MaterializeAsync below) —
// the expensive half only ever runs for the specific candidate the user actually clicked. The
// CodeAction objects a listing pass constructs are cached (keyed by a fresh GUID per candidate,
// replaced wholesale on the NEXT listing pass) so a later MaterializeAsync call doesn't need to
// re-discover/re-register anything — just a dictionary lookup plus the one GetOperationsAsync call
// this specific candidate needed all along.
//
// The tricky part is that CSharpIntelliSenseService's Roslyn document is not the user's literal
// script text: it's `_preamble + code` (a block of default `using` directives prepended so
// completion/diagnostics work without the user typing them). A fix provider operating on that
// synthetic document may insert a new `using` directive among the preamble's own — i.e. entirely
// *before* the offset we'd naively slice at to recover "the user's code" — so it can't be
// recovered by just cutting the new full text at the old preamble length. Text changes are instead
// split into "touches the preamble" (a pure `using X;` insertion there is lifted out into
// IMaterializedFix.UsingsToAdd, per docs/ImplementationPlanRPad.md — usings are project-level
// metadata, not text in the visible script) and "touches the user's code" (shifted back by
// -preambleLength and applied directly to produce NewCode). Any preamble edit that isn't a
// recognizable pure `using` insertion (replaces/deletes preamble text, or something else entirely)
// is treated as unsafe to reconstruct and the whole fix is discarded.
internal sealed class RoslynCodeFixEngine
{
    private readonly IReadOnlyList<CodeFixProvider> _providers;
    private readonly IReadOnlyList<CodeRefactoringProvider> _refactoringProviders;
    private readonly ILogger _logger;

    // Two independent caches (not one shared, keys-only-unique-by-luck dictionary): code fixes and
    // refactorings are listed on completely different triggers (text change vs caret move — see
    // EditorTabItemControl's own diagnosticFixEntries/refactoringFixEntries split) and
    // must not invalidate each other — computing a fresh refactoring list on caret move has no
    // business silently expiring the still-valid, still-displayed diagnostic fixes, and vice versa.
    // Guarded by _cacheLock since a listing pass (background Task.Run) and a materialize call
    // (triggered by a UI click, its own async flow) can race.
    private readonly object _cacheLock = new();
    private Document? _fixDocument;
    private Dictionary<string, CodeAction> _fixActions = new();
    private Document? _refactoringDocument;
    private Dictionary<string, CodeAction> _refactoringActions = new();

    // Refactoring providers that only make sense for a multi-file project on disk. MoveType offers
    // "Move type to C.cs", "Rename file to C.cs" and — keyed to CSharpIntelliSenseService's synthetic
    // document name — "Rename type to Script" on every type declaration (confirmed via a live probe).
    // A .inv script is one self-contained file the user never sees as
    // Script.cs, so all three are meaningless there. Matched by the provider's own type name
    // (MoveTypeCodeRefactoringProvider is internal to Microsoft.CodeAnalysis.Features, no public type
    // to compare against) rather than by titles — covers every variant the provider can produce.
    private static readonly HashSet<string> ExcludedRefactoringProviderTypeNames = ["MoveTypeCodeRefactoringProvider"];

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

        // Same MEF composition (InvokletRoslynHost already pulls in Microsoft.CodeAnalysis(.CSharp).
        // Features, where both CodeFixProvider AND CodeRefactoringProvider parts live) — same
        // [ExportCodeRefactoringProvider]-attribute language filter, same reasoning.
        var refactoringProviders = new List<CodeRefactoringProvider>();
        foreach (var provider in compositionHost.GetExports<CodeRefactoringProvider>())
        {
            if (ExcludedRefactoringProviderTypeNames.Contains(provider.GetType().Name)) continue;
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
        var cache = new Dictionary<string, CodeAction>();

        foreach (var group in diagnostics.Where(d => d.Location.IsInSource).GroupBy(d => d.Id))
        {
            var providers = _providers.Where(p => p.FixableDiagnosticIds.Contains(group.Key)).ToList();
            if (providers.Count == 0) continue;

            // User report 2026-07-21: analyzers like UseCollectionInitializer
            // (IDE0028) report a whole CLUSTER of same-ID diagnostics for one logical suggestion —
            // one "primary" diagnostic on the object-creation expression, plus one WellKnownDiagnostic
            // Tags.Unnecessary-tagged "companion" diagnostic on each subsequent statement that will be
            // folded in (`new List<long>()` / `a.Add(2);` / `a.Add(3);` / `a.Add(4);` — confirmed via
            // a live probe: the companions carry the SAME diagnostic ID, Hidden severity, and the
            // Unnecessary tag; the primary doesn't). Every companion is independently "fixable" by the
            // same provider, which is why the lightbulb used to show once PER LINE instead of once on
            // the collection's own declaration. When this group contains at least one non-Unnecessary
            // ("primary") diagnostic, skip the Unnecessary-tagged companions entirely — they're purely
            // fading/graying hints for the editor, not meant to independently trigger their own
            // lightbulb, and the primary's own fix already covers the whole statement group. A group
            // where EVERY diagnostic is Unnecessary-tagged (e.g. IDE0005 "remove unnecessary usings" —
            // each unused `using` is its own independently actionable diagnostic, no separate primary
            // location to redirect to) is left untouched — there's no primary to defer to.
            var groupList = group.ToList();
            var diagnosticsToProcess = groupList.Any(d => !d.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary))
                ? groupList.Where(d => !d.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary))
                : groupList;

            foreach (var diagnostic in diagnosticsToProcess)
            {
                ct.ThrowIfCancellationRequested();

                var userLine = diagnostic.Id == RemoveUnnecessaryImportsFixableId
                    ? FirstUnnecessaryUserUsingLine(diagnostics, userCode, preambleLength)
                    : GetLineNumber(userCode, diagnostic.Location.SourceSpan.Start - preambleLength);
                if (userLine < 1) continue;

                foreach (var provider in providers)
                {
                    await CollectCandidatesAsync(provider, document, diagnostic, userLine,
                        ToContractSeverity(diagnostic.Severity), result, cache, ct);
                }
            }
        }

        // User report 2026-07-27: a line with more than one undefined type fixed by the SAME using
        // directive ("var output = JsonSerializer.Serialize(result, new JsonSerializerOptions
        // {...})" — both JsonSerializer and JsonSerializerOptions need `using System.Text.Json;`)
        // produced two identical-looking "using System.Text.Json;" entries on that line — one per
        // undefined-type diagnostic, each independently discovering the same fix. Keeping only the
        // first candidate for each (Line, Title) pair is safe: Roslyn's own fix titles are
        // parameterized by the specific symbol/type/namespace involved, so two DIFFERENT actions on
        // the same line practically never render identically — a repeat is always the same action
        // reached through a different diagnostic instance.
        var seen = new HashSet<(int Line, string Title)>();
        result = result.Where(fix => seen.Add((fix.Line, fix.Title))).ToList();

        PublishFixes(document, cache, ct);
        return result;
    }

    // A superseded pass (cancelled by the next keystroke, its results ignored by the editor) must not
    // replace the cache the entries on screen point into, or clicking one silently does nothing.
    // Checked under the lock: once the newer pass has cancelled this one, it can only be overwritten by
    // that newer pass.
    private void PublishFixes(Document document, Dictionary<string, CodeAction> cache, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            ct.ThrowIfCancellationRequested();
            _fixDocument = document;
            _fixActions = cache;
        }
    }

    private void PublishRefactorings(Document document, Dictionary<string, CodeAction> cache, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            ct.ThrowIfCancellationRequested();
            _refactoringDocument = document;
            _refactoringActions = cache;
        }
    }

    // ReSharper-style "resolve all missing usings" — for each
    // unresolved-type diagnostic, materializes its candidate fixes and keeps only the ones that
    // resolved to a PURE import (no change to the user's own code — TryMaterializeAsync's own no-op
    // check already recognizes this same shape: NewCode unchanged, one namespace to add). A
    // diagnostic with more than one distinct candidate namespace is genuinely ambiguous — silently
    // picking one could import the wrong type — so those are left alone for the user to resolve one
    // at a time via the normal lightbulb, same as ComputeFixesAsync would show them.
    public async Task<IReadOnlyList<string>> ResolveAllMissingUsingsAsync(
        Document document, IReadOnlyList<Diagnostic> diagnostics, string userCode, int preambleLength, CancellationToken ct)
    {
        var resolved = new HashSet<string>(StringComparer.Ordinal);

        foreach (var diagnostic in diagnostics.Where(d => d.Location.IsInSource))
        {
            ct.ThrowIfCancellationRequested();

            var providers = _providers.Where(p => p.FixableDiagnosticIds.Contains(diagnostic.Id)).ToList();
            if (providers.Count == 0) continue;

            var candidateNamespaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var provider in providers)
            {
                var actions = new List<CodeAction>();
                var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), ct);
                try { await provider.RegisterCodeFixesAsync(context); }
                catch (OperationCanceledException) { throw; } // routine — superseded by the next keystroke
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Code fix provider {Provider} threw for {Id}", provider.GetType().Name, diagnostic.Id);
                    continue;
                }

                foreach (var leaf in actions.SelectMany(Flatten))
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsUnsupportedLeaf(leaf)) continue;

                    var materialized = await TryMaterializeAsync(leaf, document, preambleLength, userCode.Length, ct);
                    if (materialized is { } m && m.NewCode == userCode && m.UsingsToAdd.Count == 1)
                        candidateNamespaces.Add(m.UsingsToAdd[0]);
                }
            }

            if (candidateNamespaces.Count == 1)
                resolved.Add(candidateNamespaces.Single());
        }

        return resolved.ToList();
    }

    // The position-based counterpart to
    // ComputeFixesAsync above: no diagnostic to key off, no FixableDiagnosticIds to pre-filter by, so
    // every registered provider gets a chance at the given span every call (same "a couple hundred
    // providers, once per tab, already realized in the constructor" cost model as code fixes —
    // ComputeRefactoringsAsync itself only iterates the list, doesn't re-discover it). Reuses
    // Flatten/the candidate-listing shape verbatim: a CodeRefactoringContext's registered CodeAction
    // is materialized through the exact same preamble-relocation logic a CodeFixContext's is —
    // RegisterRefactoring vs RegisterCodeFix is just a different registration callback name for the
    // same underlying CodeAction type.
    public async Task<IReadOnlyList<ICodeFixInfo>> ComputeRefactoringsAsync(
        Document document, TextSpan span, string userCode, int preambleLength, int userLine, CancellationToken ct)
    {
        var result = new List<ICodeFixInfo>();
        var cache = new Dictionary<string, CodeAction>();
        if (userLine < 1)
        {
            PublishRefactorings(document, cache, ct);
            return result;
        }

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
                    if (IsUnsupportedLeaf(leaf)) continue;

                    // No underlying diagnostic (there isn't one) to derive a severity — or a
                    // DiagnosticId (ICodeFixInfo's own doc comment) — from, so both get the same
                    // neutral/absent values CollectCandidatesAsync's own non-diagnostic-adjacent
                    // candidates get.
                    var key = Guid.NewGuid().ToString("N");
                    cache[key] = leaf;
                    result.Add(new RoslynCodeFix(userLine, leaf.Title, ContractSeverity.Info, key, DiagnosticId: null));
                }
            }
        }

        PublishRefactorings(document, cache, ct);
        return result;
    }

    // The deferred half: does the actual expensive
    // work (TryMaterializeAsync — GetOperationsAsync + diffing) for the ONE candidate key identifies,
    // looked up from whichever cache (fix or refactoring) still has it. Null if key isn't in EITHER
    // cache — either it never existed, or a newer ComputeFixesAsync/ComputeRefactoringsAsync call
    // already replaced the cache it came from (the code changed enough since the candidate was
    // listed that re-materializing against stale state wouldn't be meaningful) — same "gracefully do
    // nothing" contract as every other IntelliSense failure path in this codebase.
    public async Task<(string NewCode, IReadOnlyList<string> UsingsToAdd, IReadOnlyList<TextSpan> ChangedSpans)?> MaterializeAsync(
        string key, string userCode, int preambleLength, CancellationToken ct)
    {
        CodeAction? action;
        Document? document;
        lock (_cacheLock)
        {
            if (_fixActions.TryGetValue(key, out action)) document = _fixDocument;
            else if (_refactoringActions.TryGetValue(key, out action)) document = _refactoringDocument;
            else document = null;
        }
        if (action is null || document is null) return null;

        var materialized = await TryMaterializeAsync(action, document, preambleLength, userCode.Length, ct);
        if (materialized is null) return null;

        var (newCode, usingsToAdd, _) = materialized.Value;
        if (newCode == userCode && usingsToAdd.Count == 0)
        {
            _logger.LogDebug("Discarding fix {Title}: materialized to a no-op (NewCode == userCode, no usings)", action.Title);
            return null;
        }
        return materialized;
    }

    private async Task CollectCandidatesAsync(
        CodeFixProvider provider, Document document, Diagnostic diagnostic, int userLine, ContractSeverity severity,
        List<ICodeFixInfo> result, Dictionary<string, CodeAction> cache, CancellationToken ct)
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
                if (IsUnsupportedLeaf(leaf)) continue;

                var key = Guid.NewGuid().ToString("N");
                cache[key] = leaf;
                result.Add(new RoslynCodeFix(userLine, leaf.Title, severity, key, diagnostic.Id));
            }
        }
    }

    // Some fixes group several candidates under one parent (e.g. multiple "using X;" candidates for
    // an ambiguous/unresolved type) — only the leaves carry real operations to apply.
    private static IEnumerable<CodeAction> Flatten(CodeAction action) =>
        action.NestedActions.Length == 0 ? [action] : action.NestedActions.SelectMany(Flatten);

    // User report 2026-07-27: MdRunKind.Program's synthetic wrapper class (MdRunKindTransform.
    // ProgramWrapperClassName, "__InvokletProgram") is a REAL class in the live analysis document — the
    // user's own code is textually inside it — so caret-position-triggered refactoring providers
    // (e.g. "Generate constructor") can and do see it as the enclosing type and offer to generate
    // members on it, producing suggestions like "Generate constructor '__InvokletProgram(...)'" that
    // are meaningless to the user (they never see or write that class — see MdRunKindTransform's own
    // doc comment). The identifier is deliberately distinctive enough ("astronomically unlikely" to
    // collide with real user code, per that same comment) that a plain Title substring check safely
    // filters this out regardless of which provider produced it, without needing to enumerate every
    // provider that could theoretically reference the enclosing type by name.
    private static bool MentionsSyntheticWrapper(string title) =>
        title.Contains(MdRunKindTransform.ProgramWrapperClassName, StringComparison.Ordinal);

    // CodeActionWithOptions ("Generate overrides...", "Extract interface...", ...) needs an
    // interactive options dialog from the host (VS's member picker etc.) before it can produce any
    // operations. Invoklet hosts none, so GetOperationsAsync comes back without an ApplyChangesOperation
    // and the click silently does nothing (confirmed via a live probe: "Generate overrides..." on
    // `class C { }` materialized to null). Filtered at listing time so such items never appear.
    private static bool IsUnsupportedLeaf(CodeAction leaf) =>
        leaf is CodeActionWithOptions || MentionsSyntheticWrapper(leaf.Title);

    // No longer static — needs _logger. Used to be a bare `catch { return null; }` with zero
    // logging, unlike CollectCandidatesAsync's own catch around RegisterCodeFixesAsync right above
    // (which does log at Debug) — found only by chasing a Windows-CI-only "CodeFixesChanged fires
    // with an empty list, no log output at all" symptom (NotifyTextChanged_Should_MarkUnusedVariableFix_
    // AsWarningSeverity) back through every catch block in this pipeline. That alone wasn't enough
    // though — a first round of this same logging still showed zero messages on the next CI failure,
    // meaning the real culprit isn't an exception here at all: ApplyRelocatingPreambleEdits itself has
    // two `return null;` paths that never throw (see its own doc comment) — a discard-by-design that
    // looks IDENTICAL to "no fix existed" from the outside. Logs the raw TextChanges unconditionally
    // now, before anything can discard them, since that's the one piece of data neither round of
    // logging so far has captured.
    private async Task<(string NewCode, IReadOnlyList<string> UsingsToAdd, IReadOnlyList<TextSpan> ChangedSpans)?> TryMaterializeAsync(
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
    // The "rest" used to be reconstructed as everything from preambleLength to
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
    // be nothing but synthetic wrapper text — TrailingPad, and for MdRunKind.Program also the
    // class-closing suffix mirrored by CSharpIntelliSenseService.KindSuffix — never
    // meaningful user content, so silently dropping it from what gets deleted/replaced is safe —
    // NewText itself is untouched, only how much of the (synthetic-only) tail end of the input span
    // gets consumed is shortened.
    private (string NewCode, IReadOnlyList<string> UsingsToAdd, IReadOnlyList<TextSpan> ChangedSpans)? ApplyRelocatingPreambleEdits(
        List<TextChange> changes, SourceText originalFullText, int preambleLength, int userCodeLength)
    {
        var usingsToAdd = new List<string>();
        var userCodeChanges = new List<TextChange>();
        var userCodeEnd = preambleLength + userCodeLength;

        if (changes.Count > 0 && changes.All(c => string.IsNullOrEmpty(c.NewText))
            && changes.Any(c => c.Span.Start < preambleLength))
            return ApplyLineDeletionsAcrossPreamble(changes, originalFullText, preambleLength, userCodeLength);

        var preambleChanges = new List<TextChange>();
        foreach (var change in changes.OrderBy(c => c.Span.Start))
        {
            if (change.Span.Start >= preambleLength)
            {
                var clampedLength = Math.Max(0, Math.Min(change.Span.End, userCodeEnd) - change.Span.Start);
                userCodeChanges.Add(new TextChange(
                    new TextSpan(change.Span.Start - preambleLength, clampedLength), change.NewText ?? ""));
                continue;
            }

            // Touches the hidden preamble. Only `using X;` insertions there are recoverable — a change
            // that straddles into the user's code can't be split safely, so bail out entirely.
            if (change.Span.End > preambleLength)
            {
                _logger.LogDebug(
                    "Discarding fix: preamble-touching change [{Start}..{End}) straddles preambleLength={PreambleLength}",
                    change.Span.Start, change.Span.End, preambleLength);
                return null;
            }
            preambleChanges.Add(change);
        }

        if (preambleChanges.Count > 0)
        {
            var preambleText = originalFullText.GetSubText(new TextSpan(0, preambleLength)).ToString();
            var inserted = UsingsInsertedIntoPreamble(preambleText, preambleChanges);
            if (inserted is null)
            {
                _logger.LogDebug(
                    "Discarding fix: preamble-touching changes {Changes} aren't only `using X;` insertions",
                    string.Join(" | ", preambleChanges.Select(c => $"[{c.Span.Start}..{c.Span.End}) -> \"{Truncate(c.NewText)}\"")));
                return null;
            }
            usingsToAdd.AddRange(inserted);
        }

        var userText = originalFullText.GetSubText(new TextSpan(preambleLength, userCodeLength));
        return (userText.WithChanges(userCodeChanges).ToString(), usingsToAdd, ChangedSpans(userCodeChanges));
    }

    // Where each change ended up in the changed text: its replacement's span, shifted by what the
    // earlier changes added or removed. A deletion leaves an empty span where its text was.
    private static List<TextSpan> ChangedSpans(IEnumerable<TextChange> changes)
    {
        var spans = new List<TextSpan>();
        var shift = 0;
        foreach (var change in changes.OrderBy(c => c.Span.Start))
        {
            var length = change.NewText?.Length ?? 0;
            spans.Add(new TextSpan(change.Span.Start + shift, length));
            shift += length - change.Span.Length;
        }
        return spans;
    }

    // The namespaces a fix adds to the hidden preamble, or null if its preamble changes do anything
    // other than insert whole `using X;` lines (replace or remove an existing line, insert other text).
    // Usually such a fix arrives as one zero-length insertion of "using X;\n" at a line start, but the
    // diff Roslyn computes depends on its version (and with it on the installed SDK): a using sorted
    // into the middle of the preamble can instead come back as a minimal character diff that rewrites
    // the following lines ("IO" -> "Diagnostics", "Linq" -> "IO", ...) while still meaning "one line
    // inserted". So the changes are applied to the preamble text and the result is compared line by
    // line: every original line must still be there, in order, and every extra line must be a using.
    internal static IReadOnlyList<string>? UsingsInsertedIntoPreamble(string preambleText, IEnumerable<TextChange> changes)
    {
        var oldLines = SplitLinesKeepingTerminators(preambleText);
        var newLines = SplitLinesKeepingTerminators(SourceText.From(preambleText).WithChanges(changes).ToString());
        var usings = new List<string>();
        var next = 0;
        foreach (var line in newLines)
        {
            if (next < oldLines.Count && line == oldLines[next])
            {
                next++;
                continue;
            }

            var match = _usingInsertionRegex.Match(line);
            if (!match.Success)
                return null;
            usings.Add(match.Groups[1].Value);
        }

        return next == oldLines.Count ? usings : null;
    }

    // Deletion-only fixes that also touch the preamble — "Remove unnecessary usings" removes the
    // preamble's own unused default usings together with the user's (live probe: one TextChange,
    // [20..207) -> "" with preambleLength 184). Such a minimal diff can't be split at preambleLength:
    // its boundaries are shifted by whatever text the deleted and surviving lines share ("using "),
    // so the part past preambleLength isn't the user's deleted line at all. Instead, the fix's result
    // is matched line by line against the original: every surviving line must still be there in
    // order (a pure line deletion), deleted preamble lines are ignored — the preamble is regenerated
    // on every sync and leaving an unneeded using there can't change what the user's code means —
    // and only deleted user-code lines are applied. Anything that isn't whole-line deletion bails out.
    private (string NewCode, IReadOnlyList<string> UsingsToAdd, IReadOnlyList<TextSpan> ChangedSpans)? ApplyLineDeletionsAcrossPreamble(
        List<TextChange> changes, SourceText originalFullText, int preambleLength, int userCodeLength)
    {
        var originalText = originalFullText.ToString();
        var newLines = SplitLinesKeepingTerminators(originalFullText.WithChanges(changes).ToString());
        var userCodeEnd = preambleLength + userCodeLength;
        var userCodeChanges = new List<TextChange>();

        var next = 0;
        var offset = 0;
        foreach (var line in SplitLinesKeepingTerminators(originalText))
        {
            if (next < newLines.Count && newLines[next] == line)
            {
                next++;
            }
            else if (offset >= preambleLength && offset < userCodeEnd)
            {
                // The user's last line can run on into synthetic trailing text (TrailingPad/KindSuffix)
                // — only its user-code part is deleted, same clamping rule as the regular path.
                var end = Math.Min(offset + line.Length, userCodeEnd);
                userCodeChanges.Add(new TextChange(new TextSpan(offset - preambleLength, end - offset), ""));
            }

            offset += line.Length;
        }

        if (next != newLines.Count)
        {
            _logger.LogDebug("Discarding fix: preamble-touching deletion isn't a whole-line deletion");
            return null;
        }

        var userText = originalFullText.GetSubText(new TextSpan(preambleLength, userCodeLength));
        return (userText.WithChanges(userCodeChanges).ToString(), [], ChangedSpans(userCodeChanges));
    }

    private static List<string> SplitLinesKeepingTerminators(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    // "Remove unnecessary usings" is registered by Roslyn not on IDE0005
    // itself but on a companion "RemoveUnnecessaryImportsFixable" diagnostic whose span covers the
    // WHOLE using region of the synthetic document — starting at 0, inside the hidden preamble — so the
    // plain "start before user code, skip" rule always discarded it. Its lightbulb instead goes on the
    // first user-code line actually covered by an IDE0005 (fading) diagnostic; confirmed via a live
    // probe that IDE0005 arrives as one merged span that can start in the preamble and run into the
    // user's own usings (e.g. [14..200) with preambleLength 184). No such line — only preamble usings
    // are unnecessary — means nothing the user could see would change, so no lightbulb at all.
    private const string RemoveUnnecessaryImportsFixableId = "RemoveUnnecessaryImportsFixable";
    private const string RemoveUnnecessaryImportsId = "IDE0005";

    private static int FirstUnnecessaryUserUsingLine(
        IReadOnlyList<Diagnostic> diagnostics, string userCode, int preambleLength)
    {
        var lines = diagnostics
            .Where(d => d.Id == RemoveUnnecessaryImportsId && d.Location.IsInSource
                && d.Location.SourceSpan.End > preambleLength)
            .Select(d => GetLineNumber(userCode, Math.Max(d.Location.SourceSpan.Start, preambleLength) - preambleLength))
            .Where(line => line >= 1)
            .ToList();
        return lines.Count > 0 ? lines.Min() : -1;
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

    private sealed record RoslynCodeFix(int Line, string Title, ContractSeverity Severity, string Key, string? DiagnosticId) : ICodeFixInfo;
}
