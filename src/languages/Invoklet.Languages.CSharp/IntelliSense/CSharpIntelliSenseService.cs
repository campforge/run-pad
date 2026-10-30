using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp.IntelliSense.Analysis;
using Invoklet.Languages.CSharp.IntelliSense.Completion;
using Invoklet.Languages.CSharp.IntelliSense.Features;
using Invoklet.Languages.CSharp.IntelliSense.Roslyn;
using Invoklet.Languages.CSharp.IntelliSense.ScriptDocument;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ExecutionRequest = Invoklet.Contracts.Models.ExecutionRequest;

namespace Invoklet.Languages.CSharp.IntelliSense;

public sealed class CSharpIntelliSenseService : IIntelliSenseService
{
    // Debounce for NotifyTextChanged's background diagnostics/semantic-tokens pass — long enough
    // that fast typing doesn't re-run the full Roslyn analysis on every keystroke, short enough to
    // still feel live. Settable only by tests (InternalsVisibleTo): a test that calls
    // NotifyTextChanged exactly once has nothing to debounce, and 500ms x ~500 such tests was most of
    // Keep the shared reference set stable across analysis requests.
    internal TimeSpan BackgroundAnalysisDebounce { get; set; } = TimeSpan.FromMilliseconds(500);

    // How long Dispose waits for an in-flight background analysis pass to actually observe
    // cancellation before giving up — see Dispose's own comment for why this is bounded rather than
    // unbounded.
    private static readonly TimeSpan _backgroundAnalysisDisposeTimeout = TimeSpan.FromSeconds(5);

    // Debounce, supersede and stale re-run of the background analysis.
    private readonly AnalysisScheduler _scheduler;

    private readonly ILogger<CSharpIntelliSenseService> _logger;
    // The text of the analysis document and the offsets into it; holds no workspace.
    private readonly ScriptDocumentLayout _layout = new();
    // The workspace of the analysis, its document and the baseline the sync diffs against.
    private readonly ScriptDocumentSession _session;
    // What the project is compiled against besides the script's text.
    private readonly ScriptContext _context;
    private readonly SymbolNavigation _navigation;
    private readonly CompletionService _completion;
    private readonly RefactoringService _refactorings;
    private readonly BackgroundAnalysis _analysis;
    private readonly SignatureHelp _signatureHelp;
    private readonly BreakpointConditionAnalyzer _breakpointConditions;
    private readonly DocumentFormatter _formatter;

    // Test-only visibility (InternalsVisibleTo) — confirms the FormattingOptions.NewLine override
    // below actually takes effect on the workspace's live option set. Environment.NewLine can't be
    // forced to '\r\n' on a non-Windows dev/CI machine, so this is the closest local check for the
    // fix without a real Windows run.
    internal string FormattingNewLineOptionForTests
        => _session.Workspace.CurrentSolution.Options.GetOption(FormattingOptions.NewLine, LanguageNames.CSharp);
    private readonly RoslynCodeFixEngine _codeFixEngine;
    private readonly RoslynAnalyzerEngine _analyzerEngine;

    // Invoklet's own analyzers are experimental and off until the host turns them on.
    private volatile bool _InvokletAnalyzersEnabled;

    public void UpdateInvokletAnalyzers(bool enabled) => _InvokletAnalyzersEnabled = enabled;
    // Test-only observability (InternalsVisibleTo): confirms that the sync took the incremental WithChanges path (the lineage of the
    // SourceText kept across calls) and did not fall back to SourceText.From every time.
    internal SourceText? LastSyncedSourceTextForTests => _session.LastSyncedSourceText;

    // Test-only: called by a completion once it holds its document snapshot, before it reads it.
    internal Action? AnalysisSnapshotTakenForTests
    {
        get => _analysis.SnapshotTakenForTests;
        set => _analysis.SnapshotTakenForTests = value;
    }

    internal Action? CompletionSnapshotTakenForTests
    {
        get => _completion.SnapshotTakenForTests;
        set => _completion.SnapshotTakenForTests = value;
    }

    /// <param name="referencePaths">The framework's assemblies, and others outside their folder.</param>
    /// <param name="additionalReferencePaths">Assemblies kept whatever the script's target framework,
    /// such as Invoklet.Runtime.</param>
    public CSharpIntelliSenseService(IEnumerable<string> referencePaths, ILogger<CSharpIntelliSenseService> logger,
        IEnumerable<string>? additionalReferencePaths = null)
    {
        _logger = logger;
        _scheduler = new AnalysisScheduler(
            () => BackgroundAnalysisDebounce,
            work => RoslynMessageCulture.RunAsync(work, CancellationToken.None),
            ex => _logger.LogWarning(ex, "Background analysis failed"),
            _backgroundAnalysisDisposeTimeout);

        var references = ReferenceSet.Classify(referencePaths, additionalReferencePaths);
        _session = new ScriptDocumentSession(_layout, references.All.Select(p => (MetadataReference)ScriptContext.CreateReference(p)).ToList());
        _context = new ScriptContext(_session, _layout, references, logger);
        _codeFixEngine = new RoslynCodeFixEngine(InvokletRoslynHost.CompositionHost, logger);
        _analyzerEngine = new RoslynAnalyzerEngine(logger);
        _completion = new CompletionService(_session, SyncDocument, _layout, _context, logger);
        _navigation = new SymbolNavigation(SyncDocument, _layout, _context, logger);
        _signatureHelp = new SignatureHelp(SyncDocument, _layout, logger);
        _breakpointConditions = new BreakpointConditionAnalyzer(SyncDocument, _layout);
        _formatter = new DocumentFormatter(_session, _layout, logger);
        _refactorings = new RefactoringService(_session, SyncDocument, _layout, _codeFixEngine, logger);
        _analysis = new BackgroundAnalysis(_session, SyncDocument, _layout, _context, _codeFixEngine, _analyzerEngine, () => _InvokletAnalyzersEnabled);
    }

    public void UpdateGlobalUsings(IReadOnlyList<string> usings)
    {
        if (!_layout.SetExtraUsings(usings)) return;
        // force re-sync with the new preamble on the next analysis pass — also drop the cached
        // _lastSourceText/_lastFullText baseline (see their own doc comment): the preamble just
        // changed underneath it, so diffing against it would compute a TextChange against text the
        // workspace no longer holds.
        _session.InvalidateBaseline();
    }

    public void UpdateKind(MdRunKind kind)
    {
        if (!_layout.SetKind(kind)) return;
        // force re-sync with the new kind-transform prefix on the next analysis pass — same
        // baseline-invalidation reasoning as UpdateGlobalUsings above.
        _session.InvalidateBaseline();
    }

    // ── Document sync ──────────────────────────────────────────────────────────

    private Document SyncDocument(string code)
    {
        lock (_session.SyncLock)
        {
            _context.RequireContractSources(ConnectionContractAssemblyCache.RequiresSourceCompilation(code));
            return _session.Sync(code);
        }
    }

    // Smallest single TextChange that turns oldText into newText: the common leading prefix and
    // trailing suffix are left untouched, and everything between them is replaced wholesale. Not a
    // real diff algorithm (no attempt to find a minimal edit script across multiple disjoint
    // changes) — deliberately so, since the one thing this needs to guarantee is "SourceText.WithChanges
    // applied to oldText reproduces newText exactly", and a single prefix/suffix-trimmed span already
    // does that for the actual call pattern here (one contiguous edit per keystroke — a typed
    // character, a paste, a delete — not simultaneous edits in unrelated parts of the document).
    // Internal (not private) + InternalsVisibleTo, same "internal test hook" pattern as
    // CodeFixMargin.WorstSeverityOnLine — lets ComputeMinimalChangeTests exercise the edit-span math
    // directly against edge cases (identical text, pure insert/delete, full replace, empty strings)
    // without needing to drive a real SyncDocument call through the whole AdhocWorkspace.
    internal static TextChange ComputeMinimalChange(string oldText, string newText)
        => ScriptDocumentProjection.ComputeMinimalChange(oldText, newText);

    // ── Completion ──

    public Task<ICompletionList?> GetCompletionsAsync(
        string code, int position, CompletionTrigger trigger = default, CancellationToken ct = default)
        => _completion.GetCompletionsAsync(code, position, trigger, ct);

    public Task<ISignatureHelpResult?> GetSignatureHelpAsync(
        string code, int position, char? triggerCharacter = null, CancellationToken ct = default)
        => _signatureHelp.GetSignatureHelpAsync(code, position, triggerCharacter, ct);

    // ── Quick Info, highlights, rename, go to definition and implementation ─────────────

    public Task<IQuickInfoResult?> GetQuickInfoAsync(string code, int position, CancellationToken ct = default)
        => _navigation.GetQuickInfoAsync(code, position, ct);

    public Task<IReadOnlyList<IDocumentHighlightSpan>?> GetDocumentHighlightsAsync(
        string code, int position, CancellationToken ct = default)
        => _navigation.GetDocumentHighlightsAsync(code, position, ct);

    public Task<IRenameInfo?> GetRenameInfoAsync(string code, int position, CancellationToken ct = default)
        => _navigation.GetRenameInfoAsync(code, position, ct);

    public Task<IDefinitionResult?> GetDefinitionAsync(string code, int position, CancellationToken ct = default)
        => _navigation.GetDefinitionAsync(code, position, ct);

    public Task<IReadOnlyList<IImplementationLocation>?> GetImplementationsAsync(
        string code, int position, CancellationToken ct = default)
        => _navigation.GetImplementationsAsync(code, position, ct);

    // ── Breakpoint conditions and format ─────────────────────────────────────────────────────────────────

    public Task<BreakpointConditionAnalysis?> AnalyzeBreakpointConditionAsync(string code, int position,
        string expression, CancellationToken cancellationToken = default)
        => _breakpointConditions.AnalyzeBreakpointConditionAsync(code, position, expression, cancellationToken);

    public Task<string?> FormatDocumentAsync(string code, CancellationToken ct = default) => _formatter.FormatDocumentAsync(code, ct);

    public Task<bool> IsEmptyTypeBodyPositionAsync(string code, int position, CancellationToken cancellationToken = default)
        => Task.Run(() => CSharpMemberPosition.IsInEmptyTypeBody(code, position, cancellationToken), cancellationToken);

    public void UpdateIndentation(bool useTabs, int indentationSize) => _session.UpdateIndentation(useTabs, indentationSize);

    // ── Refactorings, fixes and missing usings ─────────────

    public Task<IReadOnlyList<ICodeFixInfo>?> GetRefactoringsAsync(string code, int position, CancellationToken ct = default)
        => _refactorings.GetRefactoringsAsync(code, position, ct);

    public Task<IMaterializedFix?> MaterializeFixAsync(string code, string key, CancellationToken ct = default)
        => _refactorings.MaterializeFixAsync(code, key, ct);

    internal Task<string> ReindentOutsideTheWrapperAsync(string newCode, IReadOnlyList<TextSpan> changedSpans, CancellationToken ct)
        => _refactorings.ReindentOutsideTheWrapperAsync(newCode, changedSpans, ct);

    public Task<IMaterializedFix> ResolveAllMissingUsingsAsync(string code, CancellationToken ct = default)
        => _refactorings.ResolveAllMissingUsingsAsync(code, ct);

    // ── Background analysis (diagnostics + semantic tokens) ───────────────────

    public event Action<string, IReadOnlyList<IDiagnosticInfo>>? DiagnosticsChanged
    {
        add => _analysis.DiagnosticsChanged += value;
        remove => _analysis.DiagnosticsChanged -= value;
    }

    public event Action<string, IReadOnlyList<ISemanticToken>>? SemanticTokensChanged
    {
        add => _analysis.SemanticTokensChanged += value;
        remove => _analysis.SemanticTokensChanged -= value;
    }

    public event Action<string, IReadOnlyList<ICodeFixInfo>>? CodeFixesChanged
    {
        add => _analysis.CodeFixesChanged += value;
        remove => _analysis.CodeFixesChanged -= value;
    }

    public event Action<string, IReadOnlyList<IInlayHint>>? InlayHintsChanged
    {
        add => _analysis.InlayHintsChanged += value;
        remove => _analysis.InlayHintsChanged -= value;
    }

    public void NotifyTextChanged(string code) => NotifyTextChanged(() => code);

    // The code is materialized only here, after the debounce and off the caller's thread — a
    // notification superseded within BackgroundAnalysisDebounce (the next keystroke) never builds its
    // string at all.
    public void NotifyTextChanged(Func<string> getCode) => _scheduler.Notify(getCode, _analysis.AnalyzeAsync);

    /// <summary>Updates the typed <c>Arguments</c> members offered by completion.</summary>
    public void UpdateArgumentContract(MdRunArgumentContract? contract) => _context.UpdateArgumentContract(contract);

    public void UpdateExecutionContext(ExecutionRequest request) => _context.UpdateExecutionContext(request);

    public void ApplyExecutionContext(ExecutionRequest request, CancellationToken cancellationToken)
        => _context.ApplyExecutionContext(request, cancellationToken);

    // Without a build context: the runtime IntelliSense itself runs on, the user's NuGet settings.
    public void UpdatePackageReferences(IReadOnlyDictionary<string, Package> packages) => _context.UpdatePackageReferences(packages);

    public void UpdatePackageReferences(IReadOnlyDictionary<string, Package> packages, PackageReferenceContext context)
        => _context.UpdatePackageReferences(packages, context);

    internal Action<ExecutionRequest>? ExecutionContextResolvedForTests
    {
        get => _context.ExecutionContextResolvedForTests;
        set => _context.ExecutionContextResolvedForTests = value;
    }

    // Test-only (InternalsVisibleTo): the package assemblies resolved for the packages — the workspace
    // references them unless the framework provides the same assembly at a higher version.
    internal IReadOnlyList<string> PackageReferencePathsForTests
    {
        get => _context.PackageReferencePaths;
    }

    // Test-only (InternalsVisibleTo): the source generator assemblies the workspace runs.
    internal IReadOnlyList<string> GeneratorPathsForTests
    {
        get
        {
            lock (_session.SyncLock)
                return _session.Workspace.CurrentSolution.GetProject(_session.DocumentId.ProjectId)!.AnalyzerReferences
                    .Select(r => r.FullPath!).ToList();
        }
    }

    // Test-only (InternalsVisibleTo): every assembly the workspace currently references.
    internal IReadOnlyList<string> ReferencePathsForTests
    {
        get
        {
            lock (_session.SyncLock)
                return _session.Workspace.CurrentSolution.GetProject(_session.DocumentId.ProjectId)!.MetadataReferences
                    .OfType<PortableExecutableReference>().Select(r => r.FilePath!).ToList();
        }
    }

    // Cancel() alone only *requests* cooperative cancellation — NotifyTextChanged's Task.Run body can
    // still be mid-flight (e.g. deep inside RoslynCodeFixEngine.ComputeFixesAsync, which doesn't poll
    // the token between individual CodeFixProviders) when this method would otherwise return
    // immediately, racing _session.Workspace.Dispose() right below and continuing to burn CPU on a ThreadPool
    // thread after this service is considered "gone" by its caller. Observed as a same-process,
    // cross-test CPU-starvation flake in CI (background analysis from an earlier, already-disposed
    // test's service still running while a later test's own NotifyTextChanged competes for ThreadPool
    // capacity) — worse on Windows CI's weaker/more contended runners, but a real bug regardless of
    // platform: any caller that disposes right after only DiagnosticsChanged fires (which happens
    // *before* the codefix half of the same background pass, see ComputeDiagnosticsAndCodeFixesAsync)
    // was already at risk of this. Bounded, not unbounded — a pathological provider that truly never
    // checks its token shouldn't be able to hang Dispose (or, in production, a tab close) forever.
    public void Dispose()
    {
        _analysis.Stop();
        _scheduler.Stop();
        _session.Workspace.Dispose();
    }

    // ── Private model types ────────────────────────────────────────────────────


}
