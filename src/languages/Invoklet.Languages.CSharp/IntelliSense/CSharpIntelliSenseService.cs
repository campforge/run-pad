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

public sealed partial class CSharpIntelliSenseService : IIntelliSenseService
{
    // Default usings the synthetic script document is built with, so completion/diagnostics work
    // without the user typing them — must match the compiler's own implicit usings exactly (see
    // ImplicitUsings.Default), otherwise IntelliSense shows no error for something
    // that won't actually compile. Extended at runtime with the .rpad front matter's own
    // `namespaces:` list (see UpdateGlobalUsings) so IntelliSense agrees with what the compiler
    // will actually add as `global using` too — see docs/ImplementationPlanRPad.md and RoslynCodeFixEngine.
    //
    // "Invoklet.Runtime" appended separately from ImplicitUsings.Default itself (not folded into that
    // list) — ImplicitUsings.Default's own job is specifically mirroring the .NET SDK's fixed
    // <ImplicitUsings>enable</ImplicitUsings> set; Invoklet.Runtime (DumpContainer/ICustomMemberProvider/
    // DumpOptions — for LINQPad Runtime API compatibility) is Invoklet's own
    // addition, emitted into the real compiled project via CSharpProjectProfile.DefaultImportNamespaces
    // instead, which this needs to mirror for the same "IntelliSense must agree with what actually
    // compiles" reason as everything else on this list.
    private static readonly string[] _basePreambleUsings = [.. ImplicitUsings.Default, "Invoklet.Runtime"];

    // Also used for signature-help method/parameter signatures and quick info, not just completion —
    // IncludeParamsRefOut/IncludeDefaultValue make those read like a real signature (`params object[]
    // args`, `string? label = null`) instead of just bare "type name" pairs.
    private static readonly SymbolDisplayFormat _briefFormat =
        new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
                SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    // Matches Roslyn's own (internal) AbstractInlineTypeHintsService.s_minimalTypeStyle exactly —
    // decompiled via ICSharpCode.Decompiler (already a dependency, see CSharpDecompilerService) to
    // confirm rather than guess, since VS/Rider's own inlay-hint formatting isn't itself public API.
    private static readonly SymbolDisplayFormat _inlayHintTypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.None,
        delegateStyle: SymbolDisplayDelegateStyle.NameOnly,
        extensionMethodStyle: SymbolDisplayExtensionMethodStyle.Default,
        parameterOptions: SymbolDisplayParameterOptions.None,
        propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
        localOptions: SymbolDisplayLocalOptions.None,
        kindOptions: SymbolDisplayKindOptions.None,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.AllowDefaultLiteral);

    private static readonly IReadOnlyList<ICompletionItem> _keywords =
        new[]
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
            "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
            "void", "volatile", "while", "async", "await", "var", "dynamic", "yield", "get", "set",
            "add", "remove", "partial", "where", "select", "from", "let", "orderby", "group", "join",
            "into", "ascending", "descending", "on", "equals", "by", "when", "init", "record",
            "with", "not", "and", "or", "nint", "nuint", "file", "scoped", "required", "global",
            "nameof", "value", "unmanaged", "notnull",
        }
        // Rank 9 — see ScopeRank: keywords sort after every real symbol, so a local/parameter/member
        // that happens to fuzzy-match the same letters doesn't
        // lose a tie-break to a keyword the user isn't actually reaching for. Priority matches —
        // lowest of all kinds, see ScopeRankPriority.
        .Select(kw => (ICompletionItem)new SimpleCompletionItem(
            kw, CompletionItemKind.Keyword, [new DisplayTextSegment(kw, SemanticTokenKind.Keyword)],
            Summary: null, SortText: $"9{kw}", Priority: 0.1, UsingsToAdd: [], Source: CompletionSourceKind.Keyword))
        .ToArray();

    // Contextual keywords with no legal meaning outside a specific syntax
    // position were being offered everywhere (e.g. "orderby"/"equals" inside an ordinary method body).
    // `where` is deliberately NOT included here — it's also the generic type-constraint clause keyword
    // (`where T : class`), which is legal in an ordinary member-declaration position, so filtering it
    // to "inside a query expression" would hide it somewhere it's actually valid.
    private static readonly HashSet<string> _queryContinuationKeywords = new(StringComparer.Ordinal)
    {
        "from", "join", "into", "let", "orderby", "select", "group", "on", "equals",
        "ascending", "descending", "by",
    };

    // get/set/add/remove only mean something inside a property/indexer/event accessor list.
    // `value` only means something inside a set/add/remove accessor body (the implicit parameter name).
    private static readonly HashSet<string> _accessorKeywords = new(StringComparer.Ordinal)
    {
        "get", "set", "add", "remove",
    };

    // The only keywords that are ever legal as a generic type argument are the predefined
    // type keywords (`List<int>`, `List<string>`, ...) plus `dynamic`/`nint`/`nuint` — every other
    // keyword in _keywords (control-flow, modifiers, LINQ, ...) is a syntax error there. `var`/`void`
    // are deliberately excluded: neither is a legal type argument (`List<var>`/`List<void>` don't
    // compile), unlike ordinary local-variable/return-type position where they're normally offered.
    private static readonly HashSet<string> _typeArgumentKeywords = new(StringComparer.Ordinal)
    {
        "bool", "byte", "char", "decimal", "double", "dynamic", "float", "int", "long", "nint",
        "nuint", "object", "sbyte", "short", "string", "uint", "ulong", "ushort",
    };

    private static bool IsKeywordApplicable(ICompletionItem keyword, SyntaxNode root, int position, bool isTypeArgumentPosition)
    {
        if (isTypeArgumentPosition)
            return _typeArgumentKeywords.Contains(keyword.DisplayText);

        if (_queryContinuationKeywords.Contains(keyword.DisplayText))
            return IsInsideQueryExpression(root, position);

        if (keyword.DisplayText == "value")
            return HasAncestorMatching(root, position, n =>
                n is AccessorDeclarationSyntax a && a.Kind() is SyntaxKind.SetAccessorDeclaration
                    or SyntaxKind.AddAccessorDeclaration or SyntaxKind.RemoveAccessorDeclaration);

        if (_accessorKeywords.Contains(keyword.DisplayText))
            return IsInsideAccessorContext(root, position);

        return true;
    }

    // True anywhere inside a generic type argument list (`List<`, `List<Str`,
    // `Dictionary<string, `, ...) — the only place where completion should be restricted to type-ish
    // symbols instead of every in-scope symbol. Reuses the same HasAncestorMatching position-1
    // fallback as every other "what syntax is the caret inside" check in this file (see
    // IsInsideQueryExpression/IsInsideAccessorContext above) since the caret while typing sits at a
    // token's end, not inside it.
    private static bool IsInsideTypeArgumentList(SyntaxNode root, int position) =>
        HasAncestorMatching(root, position, n => n is TypeArgumentListSyntax);

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

    private readonly ILogger<CSharpIntelliSenseService> _logger;
    private readonly AdhocWorkspace _workspace;
    private readonly DocumentId _documentId;

    // Test-only visibility (InternalsVisibleTo) — confirms the FormattingOptions.NewLine override
    // below actually takes effect on the workspace's live option set. Environment.NewLine can't be
    // forced to '\r\n' on a non-Windows dev/CI machine, so this is the closest local check for the
    // fix without a real Windows run.
    internal string FormattingNewLineOptionForTests
        => _workspace.CurrentSolution.Options.GetOption(FormattingOptions.NewLine, LanguageNames.CSharp);
    // The references the service was created with (the framework's, Invoklet.Runtime): package updates
    // select from these again, so a framework assembly a package displaced comes back with it. The
    // framework part — the assemblies in System.Runtime.dll's directory — is replaced by the script
    // target's reference assemblies when its targeting pack is found, as the build compiles against it.
    // The additional references are never part of it, wherever they are: a self-contained Invoklet ships
    // Invoklet.Runtime.dll in the very folder its runtime's System.Runtime.dll is in.
    private readonly List<string> _baseReferencePaths = [];
    private readonly List<string> _defaultFrameworkPaths = [];
    private readonly List<string> _otherBaseReferencePaths = [];
    private IReadOnlyList<string> _frameworkPaths = [];
    // The target pack's source generators ([GeneratedRegex], System.Text.Json, [LibraryImport]), which
    // the build runs too. The workspace runs them while compiling for analysis, off the UI thread.
    private IReadOnlyList<string> _generatorPaths = [];
    // The script's own .dll references with the same-folder dependencies the build adds.
    private IReadOnlyList<string> _userReferencePaths = [];
    private CSharpParseOptions _parseOptions = CSharpScriptCompiler.ParseOptions(Environment.Version.Major, Environment.Version.Minor);
    private readonly List<MetadataReference> _nugetRefs = [];
    private IReadOnlyList<string> _nugetPaths = [];
    private readonly RoslynCodeFixEngine _codeFixEngine;
    private readonly RoslynAnalyzerEngine _analyzerEngine;

    // Invoklet's own analyzers are experimental and off until the host turns them on.
    private volatile bool _InvokletAnalyzersEnabled;

    public void UpdateInvokletAnalyzers(bool enabled) => _InvokletAnalyzersEnabled = enabled;
    private string[] _extraPreambleUsings = [];
    private string _preamble = "";
    private int _preambleLength;
    private MdRunKind _kind = MdRunKind.Statements;
    private IReadOnlyList<MdRunArgumentContractMember> _argumentContract = [];
    private string _lastCode = "";

    // The full synced document
    // text (preamble + KindPrefix + code + TrailingPad) and the live SourceText it produced, kept
    // alongside _lastCode so SyncDocument can hand Roslyn a minimal TextChange (ComputeMinimalChange)
    // against _lastSourceText instead of rebuilding the whole document from a fresh string on every
    // keystroke. SourceText.WithChanges preserves lineage to the old SourceText, letting Roslyn's
    // incremental lexer/parser reuse syntax nodes outside the edited span — benchmarked at ~7x faster
    // parsing and ~2-3x faster completion on 200-1000 line scripts versus SourceText.From every time
    // (full semantic diagnostics don't benefit the same way — GetDiagnostics is binding-dominated, not
    // parsing-dominated — see RoslynCodeFixEngine's own class doc comment for the bigger win that
    // targeted). _lastSourceText is also reset to null (forcing one full SourceText.From rebuild) in
    // UpdateGlobalUsings/UpdateKind alongside their own _lastCode = "" reset: both change _preamble or
    // _kind, which feed directly into newFullText, so the cached _lastFullText baseline would no
    // longer describe what the workspace's document is about to hold, and diffing against it would
    // compute a TextChange against the wrong text. UpdatePackageReferences's own _lastCode = "" reset
    // deliberately does NOT clear these two — it only swaps MetadataReferences (symbol resolution),
    // never the document's text, so the cached baseline is still accurate there and reusing it (a
    // no-op WithChanges diff) is strictly cheaper than forcing a full reparse for no reason.
    private string _lastFullText = "";
    private SourceText? _lastSourceText;

    // Test-only observability (InternalsVisibleTo), same "internal test hook" pattern as
    // CodeFixMargin.Fixes — lets a test confirm SyncDocument actually took the incremental
    // WithChanges path (SourceText lineage preserved across calls) rather than silently falling back
    // to SourceText.From every time, via SourceText.GetChangeRanges(older) which throws unless the two
    // instances share a lineage.
    internal SourceText? LastSyncedSourceTextForTests => _lastSourceText;

    // Guards _lastCode and the AdhocWorkspace's CurrentSolution against concurrent SyncDocument
    // calls — completion/signature-help/quick-info/background-analysis each run on their own
    // Task.Run, so without this a completion request for the text at keystroke N could race a
    // still-in-flight background-analysis pass for keystroke N-2.
    private readonly object _syncLock = new();
    // Counts the changes to the project's references, options and generated documents. A background
    // analysis pass that began on an older one describes a solution that no longer exists, so it
    // publishes nothing and runs again. Changed under _syncLock only.
    private int _contextVersion;
    // Test-only: called by a background analysis pass once it holds its document snapshot, before it
    // analyses it, so a test can change the context at exactly that point.
    internal Action? AnalysisSnapshotTakenForTests { get; set; }
    // Test-only: called by a completion once it holds its document snapshot, before it reads it.
    internal Action? CompletionSnapshotTakenForTests { get; set; }
    // Test-only: called when an execution context request has done its slow lookups and is about to
    // be applied, so a test can hold an older request there while a newer one overtakes it.
    internal Action<ExecutionRequest>? ExecutionContextResolvedForTests { get; set; }
    private List<KeyValuePair<string, (DocumentId Id, string Text)>> _connectionFiles = [];
    private CancellationTokenSource? _diagnosticsCts;

    // Tracks NotifyTextChanged's own fire-and-forget Task.Run so Dispose can actually wait for it —
    // see Dispose's own comment for why this matters beyond just "clean shutdown".
    private Task? _backgroundAnalysisTask;

    // Shared across every CSharpIntelliSenseService instance (one per tab, see EditorTabsControl.
    // CreateIntelliSenseAsync) — MetadataReferences are immutable and safe to share across unrelated
    // workspaces/compilations, so without this every new tab redid the PE-header + XML-doc parsing
    // for the same ~167 default BCL reference assemblies from scratch.
    internal static readonly ConcurrentDictionary<string, MetadataReference> ReferenceCache =
        new(StringComparer.OrdinalIgnoreCase);

    // See ReferenceDocumentation — without this, GetDocumentationCommentXml() comes back empty for
    // every metadata symbol (BCL, NuGet), so the doc-comment Summary was silently null for every
    // completion/signature-help/quick-info item except symbols the script itself defines.
    private static MetadataReference CreateReference(string path) =>
        ReferenceCache.GetOrAdd(path,
            static p => MetadataReference.CreateFromFile(p, documentation: ReferenceDocumentation.Find(p)));

    /// <param name="referencePaths">The framework's assemblies, and others outside their folder.</param>
    /// <param name="additionalReferencePaths">Assemblies kept whatever the script's target framework,
    /// such as Invoklet.Runtime.</param>
    public CSharpIntelliSenseService(IEnumerable<string> referencePaths, ILogger<CSharpIntelliSenseService> logger,
        IEnumerable<string>? additionalReferencePaths = null)
    {
        _logger = logger;
        RebuildPreamble();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var additional = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in referencePaths)
            if (File.Exists(path) && seen.Add(path))
                _baseReferencePaths.Add(path);
        foreach (var path in additionalReferencePaths ?? [])
            if (File.Exists(path) && additional.Add(path) && seen.Add(path))
                _baseReferencePaths.Add(path);
        var refs = _baseReferencePaths.Select(p => (MetadataReference)CreateReference(p)).ToList();
        var frameworkDirectory = _baseReferencePaths
            .Where(p => !additional.Contains(p)
                        && string.Equals(Path.GetFileName(p), "System.Runtime.dll", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetDirectoryName).FirstOrDefault();
        foreach (var path in _baseReferencePaths)
            (frameworkDirectory is not null && !additional.Contains(path) && Path.GetDirectoryName(path) == frameworkDirectory
                ? _defaultFrameworkPaths : _otherBaseReferencePaths).Add(path);
        _frameworkPaths = _defaultFrameworkPaths;

        _workspace = new AdhocWorkspace(InvokletRoslynHost.HostServices);

        // Root cause of a Windows-CI-only bug (chased down via extensive logging — see
        // RoslynCodeFixEngine's own comments): _preamble/KindPrefix/TrailingPad are all built with
        // literal '\n' regardless of OS, but Roslyn's own FormattingOptions.NewLine defaults to
        // Environment.NewLine ('\r\n' on Windows, '\n' on Linux/macOS) whenever a workspace doesn't
        // set it explicitly. A code fix that reformats whitespace it touches (RemoveUnusedVariable
        // normalizing the blank line before the statement it deletes, observed in production) then
        // emits '\r\n' where the document only ever had '\n' — on Windows this makes the fix's edit
        // span start earlier than expected (each extra newline byte shifts it back one more),
        // straddling across preambleLength by just enough to trip RoslynCodeFixEngine.
        // ApplyRelocatingPreambleEdits' "any preamble-touching edit that isn't a pure `using X;`
        // insertion is unsafe, discard the whole fix" safety check — so the fix silently vanishes
        // instead of applying, string-identical logic and all, depending only on which OS happens to
        // be running it. Forcing '\n' here makes Roslyn's own formatting match what this document
        // actually contains everywhere, independent of OS.
        _workspace.TryApplyChanges(_workspace.CurrentSolution.WithOptions(
            _workspace.CurrentSolution.Options.WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, "\n")));

        _codeFixEngine = new RoslynCodeFixEngine(InvokletRoslynHost.CompositionHost, logger);
        _analyzerEngine = new RoslynAnalyzerEngine(logger);

        var projectId = ProjectId.CreateNewId("InvokletScript");
        _documentId = DocumentId.CreateNewId(projectId, "Script.cs");

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name: "InvokletScript",
            assemblyName: "InvokletScript",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: _parseOptions,
            metadataReferences: refs);

        _workspace.AddProject(projectInfo);
        _workspace.AddDocument(DocumentInfo.Create(
            _documentId,
            "Script.cs",
            loader: TextLoader.From(TextAndVersion.Create(
                SourceText.From(_preamble, Encoding.UTF8),
                VersionStamp.Create())),
            filePath: Path.Combine(ScriptFolder, "Script.cs")));
        // Code actions format what they write with the options an .editorconfig beside the document
        // gives, not with the formatting options passed to Formatter: this one carries the editor's
        // indentation (UpdateIndentation). It is never written to disk.
        _editorConfigId = DocumentId.CreateNewId(projectId, ".editorconfig");
        _workspace.TryApplyChanges(_workspace.CurrentSolution.AddAnalyzerConfigDocument(
            _editorConfigId, ".editorconfig", SourceText.From(EditorConfig(_useTabs, _indentationSize)),
            filePath: Path.Combine(ScriptFolder, ".editorconfig")));
    }

    private void RebuildPreamble()
    {
        var sb = new StringBuilder();
        foreach (var u in _basePreambleUsings) sb.Append("global using ").Append(u).Append(";\n");
        foreach (var u in _extraPreambleUsings) sb.Append("global using ").Append(u).Append(";\n");
        sb.Append('\n');
        _preamble = sb.ToString();
        _preambleLength = _preamble.Length;
    }

    public void UpdateGlobalUsings(IReadOnlyList<string> usings)
    {
        var extra = usings
            .Where(u => !_basePreambleUsings.Contains(u, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (extra.SequenceEqual(_extraPreambleUsings, StringComparer.Ordinal)) return;

        _extraPreambleUsings = extra;
        RebuildPreamble();
        // force re-sync with the new preamble on the next analysis pass — also drop the cached
        // _lastSourceText/_lastFullText baseline (see their own doc comment): the preamble just
        // changed underneath it, so diffing against it would compute a TextChange against text the
        // workspace no longer holds.
        lock (_syncLock) { _lastCode = ""; _lastFullText = ""; _lastSourceText = null; }
    }

    /// <summary>Updates the typed <c>Arguments</c> members offered by completion.</summary>
    private (DocumentId Id, string Text)? _argumentSource;

    public void UpdateArgumentContract(MdRunArgumentContract? contract)
    {
        var members = contract?.Members ?? [];
        lock (_syncLock)
        {
            var source = contract is { Members.Count: > 0 } ? contract.GenerateCSharpFacade() : "";
            if (_argumentContract.SequenceEqual(members) && _argumentSource?.Text == source) return;

            var project = _workspace.CurrentSolution.GetProject(_documentId.ProjectId);
            if (project is null) return;
            var solution = project.Solution;
            if (_argumentSource is { } old) solution = solution.RemoveDocument(old.Id);
            (DocumentId Id, string Text)? next = null;
            if (source.Length > 0)
            {
                var id = DocumentId.CreateNewId(_documentId.ProjectId, "Arguments.g.cs");
                solution = solution.AddDocument(id, "Arguments.g.cs", SourceText.From(GeneratedHeader + source, Encoding.UTF8));
                next = (id, source);
            }
            if (!_workspace.TryApplyChanges(solution)) return;
            _argumentContract = members;
            _argumentSource = next;
            _lastCode = "";
            _lastFullText = "";
            _lastSourceText = null;
            _contextVersion++;
        }
    }

    public void UpdateKind(MdRunKind kind)
    {
        if (_kind == kind) return;

        _kind = kind;
        // force re-sync with the new kind-transform prefix on the next analysis pass — same
        // baseline-invalidation reasoning as UpdateGlobalUsings above.
        lock (_syncLock) { _lastCode = ""; _lastFullText = ""; _lastSourceText = null; }
    }

    // The prefix ApplyKindTransform (CSharpLanguageHost) inserts before the user's code for
    // MdRunKind.Program — mirrored here so the live-analysis document contains the same wrapper-class
    // call the compiler will actually emit, otherwise Roslyn correctly (from its own point of view)
    // flags the user's Main as an unused local function (CS8321). Deliberately NOT mirroring
    // Expression's transform here — it also trims/wraps the *end* of the code, which would desync the
    // expression-source mapping. Program uses a shared insertion-only projection so moved types still map
    // to their original editor offsets.
    // Code-dependent since ProgramPrefixFor picks the call-statement shape based on whether the user's
    // own Main is static and/or Task-returning — every call site already has
    // `code` in scope (it's what's being analyzed), so this just threads it through.
    // Parsed with this document's own options (the build's symbols for its target), as the build does.
    private string KindPrefix(string code) => KindShapeFor(code).Prefix;

    // Closes the wrapper class KindPrefix opens for MdRunKind.Program — mirrored for the
    // same reason as the prefix: without it, the live-analysis document would contain a permanently
    // unclosed class declaration, breaking parsing/binding for the rest of the synced text.
    // Code-dependent for the same reason as KindPrefix — ProgramSuffixFor's synthetic trampoline shape
    // depends on whether the user's own Main is static/Task-returning.
    private string KindSuffix(string code) => KindShapeFor(code).Suffix;

    // Total offset from the start of the live-analysis document text to the start of the user's own
    // code — usings preamble plus (for MdRunKind.Program) the mirrored wrapper-class prefix above.
    private int HeaderLength(string code) => _preambleLength + KindPrefix(code).Length;

    private ProgramBodyProjection ProjectionFor(string code) => KindShapeFor(code).Projection;

    // The prefix, projection and suffix each parse the whole script, and the offset mapping below
    // runs once per classified span, diagnostic or highlighted token: computed once per text, kind
    // and parse options, not on every mapped offset.
    private sealed record KindShape(string Code, CSharpParseOptions Options, MdRunKind Kind,
        string Prefix, ProgramBodyProjection Projection, string Suffix);

    private KindShape? _kindShape;

    private KindShape KindShapeFor(string code)
    {
        lock (_syncLock)
        {
            if (_kindShape is { } shape && shape.Code == code && shape.Options == _parseOptions && shape.Kind == _kind)
                return shape;
            return _kindShape = _kind == MdRunKind.Program
                ? new KindShape(code, _parseOptions, _kind,
                    MdRunKindTransform.ProgramPrefixFor(code, _parseOptions),
                    ProgramBodyProjection.Create(code, _parseOptions),
                    MdRunKindTransform.ProgramSuffixFor(code, _parseOptions))
                : new KindShape(code, _parseOptions, _kind, "", ProgramBodyProjection.Identity(code), "");
        }
    }

    private int ToDocumentOffset(string code, int offset) => HeaderLength(code) + ProjectionFor(code).ToProjected(offset);

    private int ToUserOffset(string code, int offset)
    {
        var result = ProjectionFor(code).ToOriginal(offset - HeaderLength(code));
        return result < 0 || result > code.Length ? -1 : result;
    }

    private TextSpan? ToUserSpan(string code, TextSpan span)
    {
        var relativeStart = span.Start - HeaderLength(code);
        return relativeStart < 0 ? null : ProjectionFor(code).ToOriginal(new TextSpan(relativeStart, span.Length));
    }


    // With nothing after the user's own code, a query position sitting at the very end
    // of it (e.g. right after typing a fresh single-letter identifier at the end of the last line —
    // the exact reported repro: `var a = ...;` then `a` on its own line, nothing typed after it yet)
    // coincides with the true end of the whole synced document. Roslyn's SemanticModel.LookupSymbols
    // silently stops finding block-scoped locals/parameters right at that exact boundary — confirmed
    // directly against Roslyn (position == EndOfFileToken, whose parent is the bare CompilationUnit
    // rather than any enclosing statement) — so the just-declared local `a` dropped out of the
    // completion list entirely, leaving the implicit top-level-statement `args` parameter (which
    // isn't affected by this boundary — it resolves from a different binder chain) as the only
    // remaining "a"-prefixed candidate. A single trailing space, appended once here, moves every
    // legitimate query position (which can only ever be <= HeaderLength + code.Length) strictly
    // before the document's new true end, sidestepping the boundary entirely — confirmed empirically
    // to restore the local for that same repro. Purely whitespace, so it can't itself introduce new
    // diagnostics/tokens; RoslynCodeFixEngine is the one consumer that reconstructs "the user's code"
    // by measuring from the document's end rather than from the known userCode.Length, so it takes an
    // explicit adjustment (see ApplyRelocatingPreambleEdits) to not leak this pad into a fix's output.
    private const string TrailingPad = " ";

    // ── Document sync ──────────────────────────────────────────────────────────

    private Document SyncDocument(string code)
    {
        lock (_syncLock)
        {
            var sourceRequired = ConnectionContractAssemblyCache.RequiresSourceCompilation(code);
            if (sourceRequired != _contractSourcesNeeded)
            {
                _contractSourcesNeeded = sourceRequired;
                ApplyConnectionRepresentation();
            }
            if (_lastCode != code)
            {
                var newFullText = ScriptDocumentProjection.Create(
                    _preamble, KindPrefix(code), ProjectionFor(code).Text, KindSuffix(code), TrailingPad).FullText;

                // Hand Roslyn a
                // minimal TextChange against the previous SourceText instead of always building a fresh
                // one from a full string — see _lastFullText/_lastSourceText's own doc comment for why
                // (incremental lexing/parsing reuse) and the measured impact. Only the very first sync,
                // or the first one after a forced reset (UpdateGlobalUsings/UpdateKind), falls back to a
                // full SourceText.From — there's no prior lineage to diff against at that point.
                var text = _lastSourceText is null
                    ? SourceText.From(newFullText, Encoding.UTF8)
                    : _lastSourceText.WithChanges(ScriptDocumentProjection.ComputeMinimalChange(_lastFullText, newFullText));

                var newSolution = _workspace.CurrentSolution.WithDocumentText(_documentId, text);
                // Only remember the new code once the workspace actually reflects it — TryApplyChanges
                // can fail (e.g. a solution-version conflict from a concurrent update elsewhere), and
                // updating _lastCode unconditionally used to mean a failed apply left CurrentSolution
                // showing stale text while _lastCode claimed it was already in sync, permanently
                // skipping the sync on every later call with that same code.
                if (_workspace.TryApplyChanges(newSolution))
                {
                    _lastCode = code;
                    _lastFullText = newFullText;
                    _lastSourceText = text;
                }
            }
            return _workspace.CurrentSolution.GetDocument(_documentId)!;
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

    // ── Completion ─────────────────────────────────────────────────────────────

    public Task<ICompletionList?> GetCompletionsAsync(
        string code, int position, CompletionTrigger trigger = default, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (ICompletionList?)await TryGetCompletionsAsync(code, position, trigger, ct), ct);

    // A completion that read its solution before the references or generated classes changed lists
    // the old ones (a connection's members that are gone, none of the new): it is computed again on
    // the new solution, not shown. A few rounds at most, so a context that keeps changing cannot
    // keep a completion from ever answering.
    private async Task<ICompletionList?> TryGetCompletionsAsync(
        string code, int position, CompletionTrigger trigger, CancellationToken ct)
    {
        var version = new StrongBox<int>();
        for (var attempt = 0; ; attempt++)
        {
            var list = await ComputeCompletionsAsync(code, position, trigger, version, ct);
            if (attempt == MaxCompletionAttempts - 1 || IsCurrent(version.Value)) return list;
            ct.ThrowIfCancellationRequested();
        }
    }

    private const int MaxCompletionAttempts = 3;

    private async Task<ICompletionList?> ComputeCompletionsAsync(
        string code, int position, CompletionTrigger trigger, StrongBox<int> contextVersion, CancellationToken ct)
    {
        try
        {
            Document document;
            lock (_syncLock)
            {
                document = SyncDocument(code);
                contextVersion.Value = _contextVersion;
            }
            CompletionSnapshotTakenForTests?.Invoke();
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = ToDocumentOffset(code, position);
            return ProvideCompletions(new CompletionRequest(code, position, trigger, model, root, pos));
        }
        // A superseded request (see CodeTextEditor.ShowCompletion) cancels its token —
        // that's an expected outcome, not a failure, so it must propagate instead of being swallowed by
        // the general catch below (which would otherwise log it as a spurious warning on every fast
        // keystroke that supersedes an in-flight request).
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Completions failed"); return null; }
    }

    // Collapses same-named candidates down to one item per DisplayText (the same instance already
    // won by ScopeRank/Kind ordering everywhere this was called before), stamping the survivor with
    // OverloadCount when more than one Method-kind candidate shares the name (
    // without this, accepting a name with several overloads gave no hint there was anything else
    // under it at all) and with IsOwnMember if ANY grouped candidate is declared directly on the
    // current/receiver type — e.g. Derived.Foo(int) shadowing
    // Base.Foo(string) should still render bold even though ScopeRank/Kind ordering might pick the
    // base one as the group's representative.
    private static IReadOnlyList<ICompletionItem> GroupByDisplayText(IEnumerable<ICompletionItem> items) =>
        items
            .GroupBy(i => i.DisplayText)
            .Select(g =>
            {
                var chosen = g.OrderBy(i => (int)i.Kind).First();
                if (chosen is not SimpleCompletionItem simple) return chosen;

                var overloadCount = g.Count(i => i.Kind is CompletionItemKind.Method or CompletionItemKind.ExtensionMethod);
                var isOwnMember = g.Any(i => i.IsOwnMember);
                return simple with
                {
                    OverloadCount = overloadCount > 1 ? overloadCount : simple.OverloadCount,
                    IsOwnMember = isOwnMember,
                };
            })
            .ToList();

    // The INamedTypeSymbol containing the given position — used for the general (non-dot) completion
    // branch's "is this an own member" check. Walks up from
    // GetEnclosingSymbol rather than assuming a fixed nesting depth, since top-level statements (this
    // project's primary script shape) land inside a compiler-synthesized Program class one level
    // deeper than an ordinary explicit class body would.
    private static INamedTypeSymbol? GetEnclosingType(SemanticModel model, int position)
    {
        var symbol = model.GetEnclosingSymbol(position);
        while (symbol is not null and not INamedTypeSymbol)
            symbol = symbol.ContainingSymbol;
        return symbol as INamedTypeSymbol;
    }

    private static bool IsOwnMember(ISymbol symbol, ITypeSymbol? currentType) =>
        currentType is not null && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, currentType);

    // ── Override-member stubs ────────────────────────

    // True when the word immediately before position — skipping back over the trigger space itself —
    // is the whole word "override" (not e.g. "myoverride"). Deliberately a raw string scan, not a
    // syntax-tree check: at this position nothing has been typed for the member yet, so there's no
    // stable node to anchor a tree-based check to (same rationale as GetIdentifierPrefix elsewhere in
    // this file).
    private static bool IsAfterOverrideModifier(string code, int position)
    {
        var end = Math.Clamp(position, 0, code.Length);
        while (end > 0 && code[end - 1] is ' ' or '\t') end--;

        const string keyword = "override";
        if (end < keyword.Length) return false;
        var start = end - keyword.Length;
        if (!code.AsSpan(start, keyword.Length).SequenceEqual(keyword)) return false;

        return start == 0 || !(char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_');
    }

    // Every method in currentType's base chain that's still overridable — virtual, abstract, or a
    // non-sealed override further up (itself still a valid slot to override again) — deduplicated to
    // the most-derived occurrence of each signature (walking from BaseType upward, the first hit for
    // a given OriginalDefinition wins), and excluding anything currentType has already overridden
    // itself. A sealed override is excluded outright rather than falling through to a less-derived,
    // still-overridable declaration of the same signature — sealed really does mean "stop offering
    // this one here", not "offer the next-best thing instead".
    private static IEnumerable<IMethodSymbol> GetOverridableMethods(INamedTypeSymbol currentType)
    {
        var alreadyOverridden = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var own in currentType.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsOverride))
            for (var overridden = own.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
                alreadyOverridden.Add(overridden.OriginalDefinition);

        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        for (var type = currentType.BaseType; type != null; type = type.BaseType)
        {
            foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (m.MethodKind != MethodKind.Ordinary) continue;
                if (!m.IsVirtual && !m.IsAbstract && !m.IsOverride) continue;
                if (m.IsSealed) continue;
                if (!seen.Add(m.OriginalDefinition)) continue;
                if (alreadyOverridden.Contains(m.OriginalDefinition)) continue;

                yield return m;
            }
        }
    }

    // An override's accessibility must match the overridden member's exactly — C# doesn't allow
    // changing it — so this only ever needs to round-trip Accessibility back to the keyword(s) that
    // produced it, not handle every possible value (private/NotApplicable can't reach here: an
    // overridable member is never private).
    private static string AccessibilityKeyword(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.Internal => "internal",
        _ => "protected",
    };

    private static string FormatOverrideParameter(IParameterSymbol p, SemanticModel model, int pos)
    {
        var refPrefix = p.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => "",
        };
        var paramsPrefix = p.IsParams ? "params " : "";
        return $"{paramsPrefix}{refPrefix}{p.Type.ToMinimalDisplayString(model, pos)} {p.Name}";
    }

    private static string ForwardOverrideArgument(IParameterSymbol p) => p.RefKind switch
    {
        RefKind.Ref => $"ref {p.Name}",
        RefKind.Out => $"out {p.Name}",
        RefKind.In => $"in {p.Name}",
        _ => p.Name,
    };

    // The stub template — same "${0}" cursor-marker/tab-indent syntax CodeTextEditor.ExpandSnippet
    // already expands for the hand-authored Snippets dictionary (ILanguageHostFactory.Snippets), so
    // no new insertion mechanism is needed on the editor side. An abstract member has no base
    // implementation to call, so it gets a NotImplementedException stub instead — calling base.M(...)
    // there wouldn't compile.
    private static ICompletionItem BuildOverrideStubItem(IMethodSymbol method, SemanticModel model, int pos)
    {
        var returnType = method.ReturnsVoid ? "void" : method.ReturnType.ToMinimalDisplayString(model, pos);
        var parameters = string.Join(
            ", ", method.Parameters.Select(p => FormatOverrideParameter(p, model, pos)));
        var arguments = string.Join(", ", method.Parameters.Select(ForwardOverrideArgument));
        var call = method.IsAbstract
            ? "throw new NotImplementedException();"
            : method.ReturnsVoid
                ? $"base.{method.Name}({arguments});"
                : $"return base.{method.Name}({arguments});";

        var template =
            $"{AccessibilityKeyword(method.DeclaredAccessibility)} override {returnType} {method.Name}({parameters})\n" +
            "{\n" +
            $"\t{call}${{0}}\n" +
            "}";

        return new SimpleCompletionItem(
            method.Name, CompletionItemKind.Method,
            method.ToDisplayParts(_briefFormat).ToSegments(), XmlDocSummary.GetSummary(method),
            SortText: method.Name, Priority: 1.0, UsingsToAdd: [], InsertTemplate: template,
            Source: CompletionSourceKind.Template);
    }

    // The nearest ObjectInitializerExpression ancestor of the token right before the
    // caret, same "walk up from the token before pos" idiom the dot/member-access branch above uses
    // for MemberAccessExpressionSyntax. Right after '{' or ',' (the common case — nothing typed yet
    // for the next member), that token's own Parent already IS the InitializerExpressionSyntax (open
    // brace and the SeparatedSyntaxList's separators are both direct children of it); mid-typing a
    // member name, the token's parent chain still leads up through it.
    private static InitializerExpressionSyntax? TryFindObjectInitializer(SyntaxNode root, int pos)
    {
        if (pos <= 0) return null;
        var token = root.FindToken(pos - 1);
        return token.Parent?.AncestorsAndSelf()
            .OfType<InitializerExpressionSyntax>()
            .FirstOrDefault(i => i.IsKind(SyntaxKind.ObjectInitializerExpression));
    }

    // GetTypeInfo on the object-creation EXPRESSION itself, not its .Type child node — same Roslyn
    // quirk noted in TryGetSignatureHelpAsync below: the latter comes back null for a type declared
    // in the same document being analyzed.
    private static List<ICompletionItem> BuildObjectInitializerCompletions(
        SemanticModel model, int pos, BaseObjectCreationExpressionSyntax objectCreation,
        InitializerExpressionSyntax initializer)
    {
        if (model.GetTypeInfo(objectCreation).Type is not INamedTypeSymbol type) return [];

        // Already-assigned members ("P1 = " already typed) shouldn't be offered again — LookupSymbols
        // has no idea what's already been set inside THIS particular initializer, only what's a
        // legal member of the type in general.
        var alreadyAssigned = new HashSet<string>(
            initializer.Expressions.OfType<AssignmentExpressionSyntax>()
                .Select(a => a.Left).OfType<IdentifierNameSyntax>()
                .Select(id => id.Identifier.ValueText),
            StringComparer.Ordinal);

        // LookupSymbols(pos, container: type) — same API the dot-member-access branch above uses —
        // already filters to what's accessible from this position, so no separate accessibility check
        // is needed here. Narrowed further to actually-settable members: a property needs a setter
        // (SetMethod is non-null for both `set` and `init` accessors — `with`-only init-only members
        // are still legitimately settable from an object initializer), a field needs to not be
        // readonly/const, and neither can be static (an object initializer only ever targets instance
        // members of the freshly-created instance).
        var members = model.LookupSymbols(pos, container: type)
            .Where(s => s switch
            {
                IPropertySymbol { IsStatic: false, SetMethod: not null } => true,
                IFieldSymbol { IsStatic: false, IsReadOnly: false, IsConst: false } => true,
                _ => false,
            })
            .Where(s => !alreadyAssigned.Contains(s.Name))
            .Select(s => ToItem(s, currentType: type))
            .Where(i => i != null)
            .Cast<ICompletionItem>();

        return GroupByDisplayText(members).OrderBy(i => i.SortText, StringComparer.Ordinal).ToList();
    }

    private static ICompletionItem? ToItem(ISymbol symbol, ITypeSymbol? currentType) => symbol switch
    {
        // LocalFunction alongside Ordinary/ReducedExtension — top-level statements (this project's
        // main script shape) use local functions as their primary way to structure code, so leaving
        // them out meant the single most common non-trivial identifier in a script never completed
        //.
        IMethodSymbol m when m.MethodKind is MethodKind.ReducedExtension
            => BuildExtensionMethodItem(m, currentType),
        IMethodSymbol m when m.MethodKind is MethodKind.Ordinary or MethodKind.LocalFunction
            => BuildItem(m, m.Name, CompletionItemKind.Method, IsOwnMember(m, currentType)),
        IPropertySymbol p
            => BuildItem(p, p.Name, CompletionItemKind.Property, IsOwnMember(p, currentType)),
        IFieldSymbol f when !f.IsImplicitlyDeclared
            => BuildItem(f, f.Name,
                f.ContainingType?.TypeKind == TypeKind.Enum ? CompletionItemKind.EnumMember : CompletionItemKind.Field,
                IsOwnMember(f, currentType)),
        IEventSymbol e
            => BuildItem(e, e.Name, CompletionItemKind.Event, IsOwnMember(e, currentType)),
        ILocalSymbol l
            => BuildItem(l, l.Name, CompletionItemKind.Variable),
        IParameterSymbol p
            => BuildItem(p, p.Name, CompletionItemKind.Variable),
        INamedTypeSymbol t
            => BuildItem(t, t.Name, t.TypeKind switch
            {
                TypeKind.Interface => CompletionItemKind.Interface,
                TypeKind.Enum => CompletionItemKind.Enum,
                TypeKind.Struct => CompletionItemKind.Struct,
                TypeKind.Delegate => CompletionItemKind.Delegate,
                _ => CompletionItemKind.Class,
            }),
        // Type parameters (the `T` in `void Foo<T>(T value)`) were dropped entirely — inside a
        // generic method/type's own body, `T` is a real, commonly-referenced symbol,
        // not just declaration-site syntax.
        ITypeParameterSymbol tp
            => BuildItem(tp, tp.Name, CompletionItemKind.TypeParameter),
        // Namespaces were silently dropped here despite ScopeRank already carrying a rank for them
        // (3 — below types) — LookupSymbols(pos) does return INamespaceSymbol candidates (e.g. typing
        // "Sys" offers "System" in real VS/Rider), they just never became a completion item
        //. CompletionItemKind has no dedicated "Namespace" entry — Module is the
        // closest LSP-style stand-in (VS Code/LSP itself uses Module for both).
        INamespaceSymbol ns
            => BuildItem(ns, ns.Name, CompletionItemKind.Module),
        _ => null
    };

    private static ICompletionItem BuildItem(
        ISymbol symbol, string displayText, CompletionItemKind kind, bool isOwnMember = false)
    {
        var rank = ScopeRank(symbol);
        return new SimpleCompletionItem(displayText, kind,
            symbol.ToDisplayParts(_briefFormat).ToSegments(), XmlDocSummary.GetSummary(symbol),
            SortText: $"{rank}{displayText}", Priority: ScopeRankPriority(rank), UsingsToAdd: [],
            IsOwnMember: isOwnMember);
    }

    private static ICompletionItem BuildExtensionMethodItem(IMethodSymbol method, ITypeSymbol? currentType)
    {
        var item = (SimpleCompletionItem)BuildItem(method, method.Name, CompletionItemKind.ExtensionMethod,
            IsOwnMember(method, currentType));
        // Invoklet's own extensions (Dump and the like) win a tie with an equally matching member: typing
        // `.D` on any value most often means Dump. The list order itself stays alphabetical.
        return IsInvokletRuntimeMember(method)
            ? item with { Priority = Math.Max(1.1, item.Priority), Source = CompletionSourceKind.Invoklet }
            : item with { Priority = Math.Max(0.85, item.Priority) };
    }

    private static bool IsInvokletRuntimeMember(ISymbol symbol) =>
        symbol.ContainingAssembly?.Name == "Invoklet.Runtime";

    // A type from a reference that isn't in
    // scope yet. Own tier (ScopeRank 3, between ordinary types and namespaces) rather than sharing
    // rank 2 with already-visible types, and carries Detail (the containing namespace) so the
    // completion list itself shows the ReSharper-style "(in Namespace)" annotation — see
    // EditorTabItemControl's completion-item construction. UsingsToAdd is what tells
    // EditorTabItemControl to add the `using` automatically when this item is accepted (see
    // CompletionRequestedEventArgs's consumer in EditorTabItemControl.AsyncCompletionProvider).
    private static ICompletionItem BuildImportableTypeItem(INamedTypeSymbol type)
    {
        var kind = type.TypeKind switch
        {
            TypeKind.Interface => CompletionItemKind.Interface,
            TypeKind.Enum => CompletionItemKind.Enum,
            TypeKind.Struct => CompletionItemKind.Struct,
            TypeKind.Delegate => CompletionItemKind.Delegate,
            _ => CompletionItemKind.Class,
        };
        // A type in the global namespace needs no "using" to become visible — treat it as needing no
        // import rather than emitting an empty namespace, which would otherwise surface as "(in )" in
        // the list and add an empty entry to the .rpad front matter's namespaces (later producing an
        // invalid "using ;").
        var ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();

        const int rank = 3; // own tier, see ScopeRank
        return new SimpleCompletionItem(
            type.Name, kind, type.ToDisplayParts(_briefFormat).ToSegments(), XmlDocSummary.GetSummary(type),
            SortText: $"{rank}{type.Name}", Priority: ScopeRankPriority(rank),
            UsingsToAdd: ns is null ? [] : [ns], Detail: ns, Source: CompletionSourceKind.ImportableType);
    }

    // Types from the script's references whose simple name starts with what's typed so far and
    // isn't already visible: packages and the script's own assemblies from the first letter, the
    // framework from MinFrameworkImportPrefix letters, so a letter or two doesn't list half the
    // framework. Types that share a name are all offered, each with its own namespace: the user
    // picks which `using` to add, none is chosen for them.
    private IEnumerable<INamedTypeSymbol> FindImportableTypes(
        Compilation compilation, string prefix, HashSet<string> alreadyVisible)
    {
        if (prefix.Length == 0) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in compilation.References)
        {
            if (prefix.Length < MinFrameworkImportPrefix && !IsAddedReference(reference)) continue;
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;

            foreach (var entry in GetImportableTypeIndex(assembly, reference))
            {
                if (alreadyVisible.Contains(entry.Name)) continue;
                if (!entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                // The same type can come from two references (a package that also ships in the framework).
                if (!seen.Add(entry.MetadataName)) continue;

                var type = assembly.GetTypeByMetadataName(entry.MetadataName);
                if (type == null || IsHiddenFromEditor(type)) continue;
                yield return type;
            }
        }
    }

    private const int MinFrameworkImportPrefix = 3;

    private bool IsAddedReference(MetadataReference reference) =>
        _nugetRefs.Contains(reference)
        || (reference is PortableExecutableReference { FilePath: { } path }
            && _userReferencePaths.Contains(path, StringComparer.OrdinalIgnoreCase));

    // [EditorBrowsable(Never)]: public for technical reasons, not meant to be written by hand.
    private static bool IsHiddenFromEditor(ISymbol symbol) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.Name == nameof(System.ComponentModel.EditorBrowsableAttribute)
            && a.ConstructorArguments is [{ Value: int state }] && state == (int)System.ComponentModel.EditorBrowsableState.Never);

    // Flattened index of every public importable type in a referenced assembly, built once per
    // reference and cached for the process lifetime (shared across every tab/service instance, same
    // sharing rationale as ReferenceCache — a MetadataReference is immutable, so its type index is
    // too). Walking the whole namespace tree (EnumerateNamespaceTypes) on every completion keystroke
    // made latency scale with the assembly's total public-type count; this makes each request a cheap
    // prefix scan over already-flattened plain strings instead. Entries store the metadata name
    // rather than the symbol itself, since INamedTypeSymbol instances aren't safe to reuse across
    // different Compilation instances — FindImportableTypes re-resolves only the (much smaller,
    // prefix-matched) subset via IAssemblySymbol.GetTypeByMetadataName against the current compilation.
    private static readonly ConcurrentDictionary<MetadataReference, IReadOnlyList<ImportableTypeEntry>>
        _importableTypeIndexCache = new();

    private readonly record struct ImportableTypeEntry(string Name, string MetadataName);

    private static IReadOnlyList<ImportableTypeEntry> GetImportableTypeIndex(
        IAssemblySymbol assembly, MetadataReference reference)
    {
        if (_importableTypeIndexCache.TryGetValue(reference, out var cached)) return cached;

        var entries = EnumerateNamespaceTypes(assembly.GlobalNamespace)
            .Select(t => new ImportableTypeEntry(t.Name, GetFullMetadataName(t)))
            .ToArray();
        return _importableTypeIndexCache.GetOrAdd(reference, entries);
    }

    private static string GetFullMetadataName(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace;
        return ns is { IsGlobalNamespace: false } ? $"{ns.ToDisplayString()}.{type.MetadataName}" : type.MetadataName;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNamespaceTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol childNamespace:
                    foreach (var type in EnumerateNamespaceTypes(childNamespace))
                        yield return type;
                    break;
                case INamedTypeSymbol { DeclaredAccessibility: Accessibility.Public } type:
                    yield return type;
                    break;
            }
        }
    }

    // Walks back from position over identifier characters in the user's own code (not the
    // preamble-prefixed document Roslyn sees) — the same "what word is being typed" idea as
    // CodeTextEditor.GetWordStart, just server-side and string-only (no syntax tree needed for this).
    private static string GetIdentifierPrefix(string code, int position)
    {
        var end = Math.Clamp(position, 0, code.Length);
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_'))
            start--;
        return code[start..end];
    }

    // Locals/parameters (the innermost scope) sort before members of the containing type, which sort
    // before other (in-scope) types, which sort before not-yet-imported types (rank 3, see
    // BuildImportableTypeItem), which
    // sort before namespaces — so typing a few letters shows what's actually nearby before it shows
    // something merely imported/visible, and before something that isn't even imported yet
    // ("the in-scope thing first, then a level up").
    private static int ScopeRank(ISymbol symbol) => symbol switch
    {
        ILocalSymbol or IParameterSymbol => 0,
        IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol => 1,
        INamedTypeSymbol => 2,
        INamespaceSymbol => 4,
        _ => 2,
    };

    // Whether a member found via LookupSymbols(container: type) actually belongs on the side of the
    // dot it was requested for — "DateTime." (static qualifier) vs "d." (instance qualifier).
    // Nested types are only ever reachable through the type itself (there's no such
    // thing as `instance.NestedType`), regardless of the nested type's own staticness, hence the
    // special case rather than just comparing symbol.IsStatic for everything.
    private static bool MatchesQualifierStaticness(ISymbol symbol, bool isStaticQualifier) => symbol switch
    {
        INamedTypeSymbol => isStaticQualifier,
        _ => symbol.IsStatic == isStaticQualifier,
    };

    // Generated script APIs are intentionally treated as first-class completion surfaces.  Their
    // members come from the current execution context (Arguments/Connections) or the compatibility
    // runtime (Util), so they should win ties against similarly named ordinary symbols while still
    // retaining the normal alphabetical order inside this member list.
    private static bool IsGeneratedApiRoot(ExpressionSyntax qualifier) =>
        qualifier is IdentifierNameSyntax { Identifier.ValueText: "Arguments" or "Connections" or "Util" };

    private static ICompletionItem PromoteGeneratedApiItem(ICompletionItem item) => item switch
    {
        SimpleCompletionItem simple => simple with
        {
            SortText = $"0{simple.SortText}",
            Priority = Math.Max(1.1, simple.Priority),
            Source = CompletionSourceKind.Invoklet,
        },
        _ => item,
    };

    // ICompletionItem.Priority — the editor's tie-break among items of otherwise-equal match quality
    // (see AvaloniaEdit.CodeCompletion.CompletionList.SelectItem: quality decides first, Priority only
    // breaks ties). Derived from the same ScopeRank used for SortText, rather than the UI layer's
    // former Kind == Method ? 1.0 : 0.9 hardcode — so a local sitting right next to
    // an equally-good-matching method actually wins the tie-break its ScopeRank already implies.
    private static double ScopeRankPriority(int rank) => rank switch
    {
        0 => 1.0,  // locals/parameters
        1 => 0.8,  // members
        2 => 0.6,  // types
        3 => 0.5,  // importable (not-yet-imported) types
        _ => 0.4,  // namespaces
    };

    // ── Signature Help ─────────────────────────────────────────────────────────

    public Task<ISignatureHelpResult?> GetSignatureHelpAsync(
        string code, int position, char? triggerCharacter = null, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (ISignatureHelpResult?)await TryGetSignatureHelpAsync(code, position, ct), ct);

    private async Task<ISignatureHelpResult?> TryGetSignatureHelpAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            var document = SyncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = ToDocumentOffset(code, position);
            if (IsInsideComment(root, pos) || IsInsideStringOrCharLiteral(root, pos)) return null;

            var node = root.FindNode(new TextSpan(Math.Max(0, pos - 1), 1), getInnermostNodeForTie: true);

            // The innermost call whose parentheses the caret is between: in `Outer(new Inner(|` that
            // is Inner, and once Inner's `)` is typed, Outer again. "new Foo(" (and the target-typed
            // `new(`) behaves like a method call.
            var (callLike, argumentList) = node.AncestorsAndSelf()
                .Select(n => (Call: n, List: n switch
                {
                    InvocationExpressionSyntax inv => inv.ArgumentList,
                    BaseObjectCreationExpressionSyntax oce => oce.ArgumentList,
                    _ => null,
                }))
                .FirstOrDefault(c => c.List is not null && IsInsideArgumentList(c.List, pos));
            if (callLike == null || argumentList == null) return null;

            var argIndex = CountArgumentsBefore(argumentList, pos);
            var argCount = argumentList.Arguments.Count;
            var isArgumentSlotEmpty = IsArgumentSlotEmpty(argumentList, argIndex, pos);
            var argumentName = argIndex < argCount ? argumentList.Arguments[argIndex].NameColon?.Name.Identifier.ValueText : null;

            var methods = GetCandidateMethods(model, callLike);

            if (methods.Count == 0) return null;

            var items = methods
                .Select(m => (ISignatureHelpItem)new SimpleSignatureHelpItem(m, ActiveParameter(m, argIndex, argumentName)))
                .ToList();
            var selectedIndex = SelectBestOverloadIndex(methods, argumentList, argCount, argumentName, model);

            return new SimpleSignatureHelpResult(items, selectedIndex, argIndex, isArgumentSlotEmpty);
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Signature help failed"); return null; }
    }

    // Picking the highlighted overload by argument COUNT alone means e.g.
    // Foo(1, "x") highlights Foo(int, int) over Foo(int, string) whenever the int-int overload sorts
    // first — the arg count matches either way, but only one of them actually type-checks. This is a
    // lightweight approximation of real overload resolution, not a reimplementation of it: it prefers
    // the first candidate where every already-typed argument has an implicit (or identity) conversion
    // to the corresponding parameter, and falls back to the pre-existing count-only match — unchanged
    // — for anything that approximation can't confidently call (params/named/optional arguments,
    // nothing typed yet, or no candidate type-checks perfectly).
    // A named argument is checked against the parameter of its name, and an overload without that
    // name doesn't fit.
    private static int SelectBestOverloadIndex(
        List<IMethodSymbol> methods, ArgumentListSyntax argumentList, int argCount, string? argumentName, SemanticModel model)
    {
        var typeAware = methods.FindIndex(m =>
        {
            if (m.Parameters.Length < argCount) return false;
            if (argumentName is not null && m.Parameters.All(p => p.Name != argumentName)) return false;
            for (var i = 0; i < argCount; i++)
            {
                var argument = argumentList.Arguments[i];
                var parameter = argument.NameColon is { } nameColon
                    ? m.Parameters.FirstOrDefault(p => p.Name == nameColon.Name.Identifier.ValueText)
                    : m.Parameters[i];
                if (parameter == null) return false;
                var argType = model.GetTypeInfo(argument.Expression).ConvertedType;
                if (argType == null) continue; // couldn't infer (e.g. still typing) — don't penalize
                if (!model.Compilation.ClassifyConversion(argType, parameter.Type).IsImplicit)
                    return false;
            }
            return true;
        });
        if (typeAware >= 0) return typeAware;

        var arityOnly = methods.FindIndex(m => m.Parameters.Length >= argCount
            && (argumentName is null || m.Parameters.Any(p => p.Name == argumentName)));
        return arityOnly >= 0 ? arityOnly : methods.Count - 1;
    }

    // The parameter the argument at the caret goes to: the one it names, the one at its position,
    // or a `params` tail past the last.
    private static IParameterSymbol? ActiveParameter(IMethodSymbol method, int argumentIndex, string? argumentName)
    {
        if (argumentName is not null) return method.Parameters.FirstOrDefault(p => p.Name == argumentName);
        if (method.Parameters.Length == 0) return null;
        if (argumentIndex < method.Parameters.Length) return method.Parameters[argumentIndex];
        var last = method.Parameters[^1];
        return last.IsParams ? last : null;
    }

    // A position inside a // or /* */ comment (including /// doc comments) has nothing to complete —
    // model.LookupSymbols(pos)/GetSymbolInfo don't know about lexical trivia, so without this guard
    // they'd happily return whatever's in scope while the user is just typing prose.
    private static bool IsInsideComment(SyntaxNode root, int position)
    {
        // TextSpan.Contains is half-open ([start, end)), so a position sitting exactly at the end of
        // a comment's span — which is exactly where the caret is while still typing an unterminated
        // "//..."/"/*..." at the point nothing follows it yet — doesn't count as "inside" by the
        // first check. Falling back to position - 1 catches that (by far the most common) case.
        return IsCommentTrivia(root.FindTrivia(position))
            || (position > 0 && IsCommentTrivia(root.FindTrivia(position - 1)));
    }

    private static bool IsCommentTrivia(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
            || trivia.IsKind(SyntaxKind.DocumentationCommentExteriorTrivia);

    // A position inside the text of a string/char literal has nothing to complete either — unlike
    // comments this isn't trivia, it's an ordinary token (StringLiteralToken etc.), so it needs its
    // own check rather than falling out of IsInsideComment. Interpolation holes
    // (the `{expr}` parts of `$"..."`) are deliberately NOT covered — their contents are ordinary
    // expression tokens (identifiers, dots, ...), not one of the kinds checked here, so completion
    // still works inside them, same as VS/Rider.
    private static bool IsInsideStringOrCharLiteral(SyntaxNode root, int position)
    {
        // Same half-open-span boundary concern as IsInsideComment: the caret sitting at the very end
        // of an in-progress, unterminated literal (by far the most common case while typing) is at
        // the token's end, so also check position - 1.
        return IsStringOrCharLiteralToken(root.FindToken(position))
            || (position > 0 && IsStringOrCharLiteralToken(root.FindToken(position - 1)));
    }

    private static bool IsStringOrCharLiteralToken(SyntaxToken token) =>
        token.IsKind(SyntaxKind.StringLiteralToken)
            || token.IsKind(SyntaxKind.CharacterLiteralToken)
            || token.IsKind(SyntaxKind.InterpolatedStringTextToken)
            || token.IsKind(SyntaxKind.Utf8StringLiteralToken)
            || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
            || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)
            || token.IsKind(SyntaxKind.Utf8SingleLineRawStringLiteralToken)
            || token.IsKind(SyntaxKind.Utf8MultiLineRawStringLiteralToken);

    // The caret sitting inside the NAME of a fresh declaration (a method/class/
    // property/variable/parameter the user is currently naming, e.g. "void Mai|" or "private |") isn't
    // an expression position — LookupSymbols/GetSymbolInfo don't distinguish "referencing an existing
    // symbol" from "declaring a brand-new one", so without this guard the editor would happily offer
    // (and auto-commit on the very next keystroke) some unrelated in-scope symbol that merely shares a
    // prefix with the name being typed, clobbering it. Returning null here means IsCommitCharacter is
    // safe by construction — there's simply no completion window to close over the declared name.
    private static bool IsDeclarationNamePosition(SyntaxNode root, int position)
    {
        return IsDeclarationIdentifierToken(root.FindToken(position))
            || (position > 0 && IsDeclarationIdentifierToken(root.FindToken(position - 1)));
    }

    // Every syntax kind below carries its own declared name as a plain Identifier token (not a
    // qualified/dotted NameSyntax, unlike e.g. namespace declarations — deliberately not covered here,
    // out of scope for the reported symptom) — checking `== token` (not just "is this kind of node")
    // rules out completion inside some OTHER part of the same declaration (a method's return type, a
    // parameter's type, an attribute, ...), where the caret is legitimately in expression/type position.
    private static bool IsDeclarationIdentifierToken(SyntaxToken token) => token.Parent switch
    {
        MethodDeclarationSyntax n => n.Identifier == token,
        LocalFunctionStatementSyntax n => n.Identifier == token,
        ClassDeclarationSyntax n => n.Identifier == token,
        StructDeclarationSyntax n => n.Identifier == token,
        InterfaceDeclarationSyntax n => n.Identifier == token,
        RecordDeclarationSyntax n => n.Identifier == token,
        EnumDeclarationSyntax n => n.Identifier == token,
        EnumMemberDeclarationSyntax n => n.Identifier == token,
        PropertyDeclarationSyntax n => n.Identifier == token,
        VariableDeclaratorSyntax n => n.Identifier == token,
        ParameterSyntax n => n.Identifier == token,
        TypeParameterSyntax n => n.Identifier == token,
        _ => false,
    };

    // See _queryContinuationKeywords — true when the caret sits anywhere inside a LINQ query
    // expression (from ... to the final select/group-by clause) or a query continuation
    // (`into` ... onward), the only place from/join/into/let/orderby/select/group/on/equals/
    // ascending/descending/by mean anything.
    private static bool IsInsideQueryExpression(SyntaxNode root, int position) =>
        HasAncestorMatching(root, position, n => n is QueryExpressionSyntax or QueryContinuationSyntax);

    // See _accessorKeywords — true when the caret sits inside a property/indexer/event's accessor
    // list (covers the empty "{ }" case, where the closing brace token itself belongs to the
    // AccessorListSyntax) or inside an already-parsed accessor's own body.
    private static bool IsInsideAccessorContext(SyntaxNode root, int position) =>
        HasAncestorMatching(root, position, n => n is AccessorListSyntax or AccessorDeclarationSyntax);

    // Same half-open-span boundary concern as IsInsideComment/IsInsideStringOrCharLiteral above: the
    // caret while still typing sits at a token's end (exclusive), so also check position - 1.
    private static bool HasAncestorMatching(SyntaxNode root, int position, Func<SyntaxNode, bool> predicate)
    {
        return Check(position) || (position > 0 && Check(position - 1));

        bool Check(int pos) => root.FindToken(pos).Parent?.AncestorsAndSelf().Any(predicate) == true;
    }

    private static int CountArgumentsBefore(ArgumentListSyntax argList, int position)
    {
        var count = 0;
        foreach (var comma in argList.Arguments.GetSeparators())
        {
            if (comma.SpanStart < position) count++;
            else break;
        }
        return count;
    }

    // No argument has been parsed at argIndex at all (right after `(`, or right after a
    // trailing `,` with nothing typed since — Roslyn's error recovery doesn't produce an Arguments
    // entry for either), or one has but position sits at or before its own start (caret placed back
    // at the very start of an already-typed argument, e.g. via Home/click) — either way, nothing has
    // been typed for this slot yet.
    private static bool IsArgumentSlotEmpty(ArgumentListSyntax argList, int argIndex, int position)
        => argIndex >= argList.Arguments.Count || position <= argList.Arguments[argIndex].SpanStart;

    // ── Quick Info ─────────────────────────────────────────────────────────────

    public Task<IQuickInfoResult?> GetQuickInfoAsync(
        string code, int position, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IQuickInfoResult?)await TryGetQuickInfoAsync(code, position, ct), ct);

    private async Task<IQuickInfoResult?> TryGetQuickInfoAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            var document = SyncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = ToDocumentOffset(code, position);
            if (IsInsideComment(root, pos) || IsInsideStringOrCharLiteral(root, pos)) return null;

            var token = root.FindToken(pos);
            if (token == default) return null;

            var symInfo = model.GetSymbolInfo(token.Parent!);
            var symbol = symInfo.Symbol ?? symInfo.CandidateSymbols.FirstOrDefault()
                ?? (ISymbol?)model.GetTypeInfo(token.Parent!).Type;

            if (symbol == null) return null;

            return new SimpleQuickInfoResult(
                symbol.ToDisplayParts(_briefFormat).ToSegments(), XmlDocSummary.GetSummary(symbol));
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Quick info failed"); return null; }
    }

    // ── Document Highlights ──────────────────────────────────────────────────────

    // Roslyn ships a ready-made IDocumentHighlightsService for
    // exactly this, but it (and its result types DocumentHighlights/HighlightSpan) are internal to
    // Microsoft.CodeAnalysis.Features — not reachable from here even though that assembly is already
    // loaded into InvokletRoslynHost's MEF composition (confirmed directly: referencing the type by
    // name fails to compile with CS0122, "inaccessible due to its protection level"). Reimplemented
    // with only public Roslyn API instead: find the symbol under the caret, then scan every
    // identifier token in the document for one that resolves to the same symbol.
    public Task<IReadOnlyList<IDocumentHighlightSpan>?> GetDocumentHighlightsAsync(
        string code, int position, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IReadOnlyList<IDocumentHighlightSpan>?)await TryGetDocumentHighlightsAsync(code, position, ct), ct);

    private async Task<IReadOnlyList<IDocumentHighlightSpan>?> TryGetDocumentHighlightsAsync(
        string code, int position, CancellationToken ct)
    {
        try
        {
            var found = await FindSymbolAndOccurrencesAsync(code, position, ct);
            return found?.Spans;
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Document highlights failed"); return null; }
    }

    // ── Rename ────────────────────────────────────────────────────────────────────

    public Task<IRenameInfo?> GetRenameInfoAsync(string code, int position, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IRenameInfo?)await TryGetRenameInfoAsync(code, position, ct), ct);

    private async Task<IRenameInfo?> TryGetRenameInfoAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            var found = await FindSymbolAndOccurrencesAsync(code, position, ct);
            if (found is not { } result) return null;

            // Only symbols the user's own script actually declares can be renamed — a metadata/
            // framework symbol (e.g. "WriteLine" in Console.WriteLine) has no source location to
            // rename AT; renaming its call sites in the script without renaming the framework
            // method itself would just turn correct code into a compile error.
            if (!result.Symbol.Locations.Any(IsInScript)) return null;

            return new SimpleRenameInfo(result.Symbol.Name, result.Spans);
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Rename info failed"); return null; }
    }

    // ── Go to Definition (F12) ───────────────────────────────────────────────────

    public Task<IDefinitionResult?> GetDefinitionAsync(string code, int position, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IDefinitionResult?)await TryGetDefinitionAsync(code, position, ct), ct);

    private async Task<IDefinitionResult?> TryGetDefinitionAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            var document = SyncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = ToDocumentOffset(code, position);
            if (IsInsideComment(root, pos) || IsInsideStringOrCharLiteral(root, pos)) return null;

            var token = root.FindToken(pos);
            if (!token.IsKind(SyntaxKind.IdentifierToken)) return null;

            var symbol = ResolveTokenSymbol(model, token);
            if (symbol == null) return null;

            if (symbol.Locations.FirstOrDefault(IsInScript) is { } sourceLocation)
            {
                var offset = ToUserOffset(code, sourceLocation.SourceSpan.Start);
                if (offset < 0) return null; // declared inside the prepended preamble, not user code
                return new SimpleDefinitionResult(
                    IsInSource: true, Offset: offset,
                    AssemblyPath: null, ContainingTypeFullName: null, MemberName: null,
                    ParameterTypeNames: [], DisplayName: symbol.Name);
            }

            if (GeneratedDocumentFor(symbol) is { } generated)
                return new SimpleDefinitionResult(
                    IsInSource: false, Offset: 0,
                    AssemblyPath: null, ContainingTypeFullName: null, MemberName: null,
                    ParameterTypeNames: [], DisplayName: symbol.ToDisplayString(),
                    GeneratedDocument: generated);

            // Extension methods resolve to their REDUCED form (receiver dropped from Parameters) —
            // metadata (and so the decompiler's own type system) only knows the original static
            // method, receiver included as parameter 0. Un-reduce before reading Name/Parameters so
            // the parameter list CSharpDecompilerService is asked to match against reflects reality.
            if (symbol is IMethodSymbol { ReducedFrom: { } original })
                symbol = original;

            var containingType = symbol is INamedTypeSymbol selfType ? selfType : symbol.ContainingType;
            if (containingType is null) return null; // e.g. a namespace — nothing to decompile

            var reference = model.Compilation.GetMetadataReference(containingType.ContainingAssembly);
            if (reference is not PortableExecutableReference { FilePath: { } assemblyPath }) return null;

            var isTypeItself = SymbolEqualityComparer.Default.Equals(symbol, containingType);
            var parameterTypeNames = symbol is IMethodSymbol { Parameters: var parameters }
                ? parameters.Select(p => p.Type.Name).ToArray()
                : [];

            return new SimpleDefinitionResult(
                IsInSource: false, Offset: 0,
                AssemblyPath: assemblyPath,
                ContainingTypeFullName: GetReflectionTypeName(containingType.OriginalDefinition),
                MemberName: isTypeItself ? null : symbol.Name,
                ParameterTypeNames: parameterTypeNames,
                DisplayName: symbol.ToDisplayString());
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Go to definition failed"); return null; }
    }

    // Reflection-style name (Namespace.Type`Arity+Nested`Arity) — what ICSharpCode.Decompiler's
    // FullTypeName(string) constructor expects. INamedTypeSymbol.MetadataName already carries the
    // `Arity suffix for open generics; OriginalDefinition (applied by the caller) strips any
    // constructed-generic type arguments first (e.g. List<int> -> List<T>) since a FullTypeName names
    // the open generic definition, not one particular instantiation.
    private static string GetReflectionTypeName(INamedTypeSymbol type)
    {
        if (type.ContainingType is { } outer)
            return GetReflectionTypeName(outer.OriginalDefinition) + "+" + type.MetadataName;
        return type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "." + type.MetadataName
            : type.MetadataName;
    }

    // ── Go to Implementation (Ctrl+F12) ──────────────────────────────────────────

    public Task<IReadOnlyList<IImplementationLocation>?> GetImplementationsAsync(
        string code, int position, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IReadOnlyList<IImplementationLocation>?)await TryGetImplementationsAsync(code, position, ct), ct);

    private async Task<IReadOnlyList<IImplementationLocation>?> TryGetImplementationsAsync(
        string code, int position, CancellationToken ct)
    {
        try
        {
            var document = SyncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = ToDocumentOffset(code, position);
            if (IsInsideComment(root, pos) || IsInsideStringOrCharLiteral(root, pos)) return null;

            var token = root.FindToken(pos);
            if (!token.IsKind(SyntaxKind.IdentifierToken)) return null;

            var symbol = ResolveTokenSymbol(model, token);
            if (symbol == null) return null;

            var solution = document.Project.Solution;

            // FindImplementationsAsync only covers interface members/types — an abstract or virtual
            // member (not on an interface) needs FindOverridesAsync instead. A single-file script's
            // own AdhocWorkspace only ever contains the script itself, so either call can only ever
            // surface OTHER declarations inside that same document — never metadata.
            IEnumerable<ISymbol> found = symbol is INamedTypeSymbol namedType
                ? await SymbolFinder.FindImplementationsAsync(namedType, solution, cancellationToken: ct)
                : await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct);
            var implementations = found.ToList();

            if (implementations.Count == 0 && IsOverridable(symbol))
                implementations = (await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: ct)).ToList();

            if (implementations.Count == 0) return null;

            var results = new List<IImplementationLocation>();
            foreach (var impl in implementations)
            {
                if (impl.Locations.FirstOrDefault(IsInScript) is not { } location) continue;
                var offset = ToUserOffset(code, location.SourceSpan.Start);
                if (offset < 0) continue; // inside the prepended usings preamble, not user code
                results.Add(new SimpleImplementationLocation(offset, impl.ToDisplayString()));
            }

            return results.Count == 0 ? null : results;
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Go to implementation failed"); return null; }
    }

    private static bool IsOverridable(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.IsAbstract || m.IsVirtual,
        IPropertySymbol p => p.IsAbstract || p.IsVirtual,
        IEventSymbol e => e.IsAbstract || e.IsVirtual,
        _ => false,
    };

    // Shared by GetDocumentHighlightsAsync and GetRenameInfoAsync — both are "find the symbol under
    // the caret, then every identifier token elsewhere in the document that resolves to that same
    // symbol"; they only differ in what they do with the result afterward (highlight vs. offer to
    // rename, the latter additionally filtering to source-declared symbols only).
    private async Task<(ISymbol Symbol, List<IDocumentHighlightSpan> Spans)?> FindSymbolAndOccurrencesAsync(
        string code, int position, CancellationToken ct)
    {
        var document = SyncDocument(code);
        var model = await document.GetSemanticModelAsync(ct);
        if (model == null) return null;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return null;

        var pos = ToDocumentOffset(code, position);
        if (IsInsideComment(root, pos) || IsInsideStringOrCharLiteral(root, pos)) return null;

        var token = root.FindToken(pos);
        if (!token.IsKind(SyntaxKind.IdentifierToken)) return null;

        var symbol = ResolveTokenSymbol(model, token);
        if (symbol == null) return null;

        var spans = new List<IDocumentHighlightSpan>();
        foreach (var candidate in root.DescendantTokens())
        {
            ct.ThrowIfCancellationRequested();
            // Cheap text check before the semantic lookup — most identifiers in the document
            // aren't even textually the same name, no need to ask the semantic model about them.
            if (!candidate.IsKind(SyntaxKind.IdentifierToken) || candidate.ValueText != token.ValueText)
                continue;

            var candidateSymbol = ResolveTokenSymbol(model, candidate);
            if (candidateSymbol == null || !SymbolEqualityComparer.Default.Equals(candidateSymbol, symbol))
                continue;

            var userStart = ToUserOffset(code, candidate.SpanStart);
            if (userStart < 0) continue; // inside the prepended usings preamble, not user code
            spans.Add(new SimpleDocumentHighlightSpan(userStart, candidate.Span.Length));
        }

        return (symbol, spans);
    }

    // Shared by both the caret's own token and every candidate token scanned against it — a
    // declaration site (e.g. the identifier in "var x = ...") has no GetSymbolInfo result of its
    // own (that call is for usages/references), only GetDeclaredSymbol; a usage is the opposite.
    // Trying both, in this order, covers either kind of identifier token uniformly.
    private static ISymbol? ResolveTokenSymbol(SemanticModel model, SyntaxToken token)
    {
        var symbol = model.GetSymbolInfo(token.Parent!).Symbol ?? model.GetDeclaredSymbol(token.Parent!);
        // Calls bind to a reduced extension method (without its receiver parameter); its source
        // declaration is the unreduced method. Both must identify the same rename/highlight symbol.
        return symbol is IMethodSymbol { ReducedFrom: { } method } ? method.OriginalDefinition : symbol;
    }

    // ── Format ─────────────────────────────────────────────────────────────────

    public async Task<BreakpointConditionAnalysis?> AnalyzeBreakpointConditionAsync(string code, int position,
        string expression, CancellationToken cancellationToken = default)
    {
        var document = SyncDocument(code);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model is null) return null;
        var pos = ToDocumentOffset(code, Math.Clamp(position, 0, code.Length));
        var parsed = SyntaxFactory.ParseExpression(expression);
        string? error = null;
        if (!string.IsNullOrWhiteSpace(expression))
        {
            var syntaxError = parsed.GetDiagnostics().FirstOrDefault(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            if (syntaxError is not null) error = syntaxError.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
            else
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var statement = root?.FindToken(pos).Parent?.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
                if (statement is null) error = "Choose a line inside executable code.";
                else
                {
                    // Bind in a forked document, never execute the expression or modify the editor workspace.
                    var mark = new SyntaxAnnotation();
                    var condition = parsed.WithAdditionalAnnotations(mark);
                    var probe = SyntaxFactory.IfStatement(condition, SyntaxFactory.Block());
                    var fork = document.WithSyntaxRoot(root!.ReplaceNode(statement, SyntaxFactory.Block(probe, statement)));
                    var forkRoot = await fork.GetSyntaxRootAsync(cancellationToken);
                    var forkModel = await fork.GetSemanticModelAsync(cancellationToken);
                    var bound = forkRoot!.GetAnnotatedNodes(mark).Single();
                    error = forkModel!.GetDiagnostics(cancellationToken: cancellationToken)
                        .FirstOrDefault(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
                            && d.Location.SourceSpan.IntersectsWith(bound.Span))
                        ?.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
                    if (error is null && forkModel.GetTypeInfo(bound, cancellationToken).ConvertedType?.SpecialType != SpecialType.System_Boolean)
                        error = "The condition must be a Boolean expression.";
                }
            }
        }
        var end = expression.Length;
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(expression[start - 1]) || expression[start - 1] == '_')) start--;
        var prefix = expression[start..];
        IEnumerable<ISymbol> symbols = model.LookupSymbols(pos);
        if (start > 0 && expression[start - 1] == '.')
        {
            var receiver = SyntaxFactory.ParseExpression(expression[..(start - 1)]);
            var type = model.GetSpeculativeTypeInfo(pos, receiver, SpeculativeBindingOption.BindAsExpression).Type;
            symbols = type is null ? [] : model.LookupSymbols(pos, container: type);
        }
        var suggestions = symbols.Where(s => s.CanBeReferencedByName && !s.IsImplicitlyDeclared)
            .Select(s => s.Name).Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).Take(40).ToArray();
        return new(error, suggestions);
    }

    public Task<bool> IsEmptyTypeBodyPositionAsync(string code, int position, CancellationToken cancellationToken = default)
        => Task.Run(() => CSharpMemberPosition.IsInEmptyTypeBody(code, position, cancellationToken), cancellationToken);

    private bool _useTabs;
    private int _indentationSize = 4;
    private readonly DocumentId _editorConfigId;

    // Where the script document and its .editorconfig appear to be; nothing is stored there.
    private static readonly string ScriptFolder = Path.Combine(Path.GetTempPath(), "InvokletScript");

    private static string EditorConfig(bool useTabs, int indentationSize) =>
        $"root = true\n[*.cs]\nindent_style = {(useTabs ? "tab" : "space")}\nindent_size = {indentationSize}\ntab_width = {indentationSize}\n";

    private OptionSet FormattingOptionsSnapshot() => _workspace.CurrentSolution.Options
        .WithChangedOption(FormattingOptions.UseTabs, LanguageNames.CSharp, _useTabs)
        .WithChangedOption(FormattingOptions.TabSize, LanguageNames.CSharp, _indentationSize)
        .WithChangedOption(FormattingOptions.IndentationSize, LanguageNames.CSharp, _indentationSize);

    public void UpdateIndentation(bool useTabs, int indentationSize)
    {
        lock (_syncLock)
        {
            // Applying solution options writes shared Roslyn services; keep each editor's policy local.
            _useTabs = useTabs;
            _indentationSize = Math.Clamp(indentationSize, 1, 16);
            _workspace.TryApplyChanges(_workspace.CurrentSolution.WithAnalyzerConfigDocumentText(
                _editorConfigId, SourceText.From(EditorConfig(_useTabs, _indentationSize))));
        }
    }

    // Formats in a throwaway workspace/project containing only the user's code (no preamble) so
    // Roslyn's Formatter output maps back to the editor text directly, with no preamble/code
    // boundary whitespace to reconcile.
    public async Task<string?> FormatDocumentAsync(string code, CancellationToken ct = default)
    {
        try
        {
            CSharpParseOptions parseOptions;
            OptionSet formattingOptions;
            lock (_syncLock)
            {
                parseOptions = _parseOptions;
                formattingOptions = FormattingOptionsSnapshot();
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

    // ── Refactorings (CodeRefactoringProvider hosting in the lightbulb) ─────────────

    public Task<IReadOnlyList<ICodeFixInfo>?> GetRefactoringsAsync(
        string code, int position, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IReadOnlyList<ICodeFixInfo>?)await TryGetRefactoringsAsync(code, position, ct), ct);

    private async Task<IReadOnlyList<ICodeFixInfo>?> TryGetRefactoringsAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            if (position < 0 || position > code.Length) return null;

            var document = SyncDocument(code);
            var headerLength = HeaderLength(code);
            var span = new TextSpan(ToDocumentOffset(code, position), 0);

            // 1-based, same convention as CodeFixEntry.Line/RoslynCodeFixEngine's own diagnostic-fix
            // line lookup — LinePosition.Line itself is 0-based.
            var userLine = SourceText.From(code).Lines.GetLinePosition(position).Line + 1;

            return await _codeFixEngine.ComputeRefactoringsAsync(document, span, ProjectionFor(code).Text, headerLength, userLine, ct);
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
    public Task<IMaterializedFix?> MaterializeFixAsync(string code, string key, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IMaterializedFix?)await TryMaterializeFixAsync(code, key, ct), ct);

    private async Task<IMaterializedFix?> TryMaterializeFixAsync(string code, string key, CancellationToken ct)
    {
        try
        {
            var headerLength = HeaderLength(code);
            var materialized = await _codeFixEngine.MaterializeAsync(key, ProjectionFor(code).Text, headerLength, ct);
            if (materialized is null) return null;
            var projection = ProjectionFor(code);
            if (projection.RestoreEdit(materialized.Value.NewCode, materialized.Value.ChangedSpans,
                    KindPrefix(code), KindSuffix(code), _parseOptions) is not var (restored, changedSpans)) return null;
            var newCode = restored;
            if (_kind == MdRunKind.Program && restored != code)
            {
                // A class compiled outside the hidden one was formatted at its own columns already.
                var moved = projection.HasChanges ? ProgramBodyProjection.Create(restored, _parseOptions).Moved : [];
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
        lock (_syncLock) options = FormattingOptionsSnapshot();
        var useTabs = options.GetOption(FormattingOptions.UseTabs, LanguageNames.CSharp);
        var unit = useTabs ? "\t" : new string(' ', options.GetOption(FormattingOptions.IndentationSize, LanguageNames.CSharp));
        CSharpParseOptions parseOptions;
        lock (_syncLock) parseOptions = _parseOptions;

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
    public Task<IMaterializedFix> ResolveAllMissingUsingsAsync(string code, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IMaterializedFix)await TryResolveAllMissingUsingsAsync(code, ct), ct);

    private async Task<IMaterializedFix> TryResolveAllMissingUsingsAsync(string code, CancellationToken ct)
    {
        try
        {
            var document = SyncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model is null) return new SimpleMaterializedFix(code, []);

            var headerLength = HeaderLength(code);
            var diagnostics = model.GetDiagnostics(cancellationToken: ct)
                .Where(d => d.Id is "CS0246" or "CS0103")
                .ToList();

            var usings = await _codeFixEngine.ResolveAllMissingUsingsAsync(document, diagnostics, ProjectionFor(code).Text, headerLength, ct);
            return new SimpleMaterializedFix(code, usings);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ResolveAllMissingUsingsAsync failed");
            return new SimpleMaterializedFix(code, []);
        }
    }

    // ── Background analysis (diagnostics + semantic tokens) ───────────────────

    public event Action<string, IReadOnlyList<IDiagnosticInfo>>? DiagnosticsChanged;
    public event Action<string, IReadOnlyList<ISemanticToken>>? SemanticTokensChanged;
    public event Action<string, IReadOnlyList<ICodeFixInfo>>? CodeFixesChanged;
    public event Action<string, IReadOnlyList<IInlayHint>>? InlayHintsChanged;

    public void NotifyTextChanged(string code) => NotifyTextChanged(() => code);

    // The code is materialized only here, after the debounce and off the caller's thread — a
    // notification superseded within BackgroundAnalysisDebounce (the next keystroke) never builds its
    // string at all.
    public void NotifyTextChanged(Func<string> getCode)
    {
        // Not disposed here — a superseded background analysis pass (Task.Run below) can still be
        // mid-flight past this point (e.g. inside RoslynCodeFixEngine, which doesn't poll the token
        // between individual CodeFixProviders — see Dispose's own comment), and registering/reading
        // against an already-disposed CancellationTokenSource throws ObjectDisposedException
        //. A CancellationTokenSource whose WaitHandle is never touched holds no
        // unmanaged resources, so leaving the superseded one for the GC is safe; Dispose() below
        // (this instance's own teardown, after _backgroundAnalysisTask is awaited/timed out) still
        // disposes the final one.
        _diagnosticsCts?.Cancel();
        _diagnosticsCts = new CancellationTokenSource();
        var ct = _diagnosticsCts.Token;

        _backgroundAnalysisTask = RoslynMessageCulture.RunAsync(async () =>
        {
            try
            {
                await Task.Delay(BackgroundAnalysisDebounce, ct);
                var code = getCode();
                Document document;
                int version;
                lock (_syncLock)
                {
                    document = SyncDocument(code);
                    version = _contextVersion;
                }
                AnalysisSnapshotTakenForTests?.Invoke();
                await Task.WhenAll(
                    ComputeDiagnosticsAndCodeFixesAsync(code, document, version, ct),
                    ComputeSemanticTokensAsync(code, document, version, ct),
                    ComputeInlayHintsAsync(code, document, version, ct));
                // The references or generated classes changed while this pass ran: what it found
                // describes the old ones and was not published. Run again on the new solution,
                // unless a newer notification has taken over already.
                if (!IsCurrent(version) && !ct.IsCancellationRequested) NotifyTextChanged(code);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Background analysis failed"); }
        }, CancellationToken.None);
    }

    private bool IsCurrent(int version)
    {
        lock (_syncLock) return version == _contextVersion;
    }

    // Whether a result may be published: a newer text notification cancels the pass, and a change to
    // the project's context makes its solution obsolete. Checked right before each event, since the
    // analysis itself may finish after either.
    private bool CanPublish(int version, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return IsCurrent(version);
    }

    private async Task ComputeSemanticTokensAsync(string code, Document document, int version, CancellationToken ct)
    {
        var headerLength = HeaderLength(code);
        var textLen = headerLength + ProjectionFor(code).Text.Length;
        var spans = await Classifier.GetClassifiedSpansAsync(
            document, new TextSpan(0, textLen), ct);

        var result = new List<ISemanticToken>();
        foreach (var span in spans)
        {
            var kind = ClassifySpan(span.ClassificationType);
            if (kind == SemanticTokenKind.None) continue;

            var mapped = ToUserSpan(code, span.TextSpan);
            if (mapped is null || mapped.Value.IsEmpty) continue;
            var start = mapped.Value.Start;
            var end = mapped.Value.End;

            result.Add(new SimpleSemanticToken(start, end - start, kind));
        }

        if (CanPublish(version, ct)) SemanticTokensChanged?.Invoke(code, result);
    }

    // Inferred `var x` / `foreach (var x in ...)` type annotations only
    // (Roslyn's own CSharpInlineTypeHintsService also covers lambda parameters, implicit `new()`, and
    // collection expressions — internal API, decompiled to confirm exact placement/format below, not
    // ported wholesale; those three are lower-value, left for a follow-up if wanted). Single-variable
    // declarations only (`var a = 1, b = 2;` skipped entirely) — matches Roslyn's own behavior; a
    // multi-declarator statement can only have one inferred type anyway, so hinting just the first
    // variable would misleadingly suggest it doesn't apply to the rest.
    private async Task ComputeInlayHintsAsync(string code, Document document, int version, CancellationToken ct)
    {
        var model = await document.GetSemanticModelAsync(ct);
        var root = await document.GetSyntaxRootAsync(ct);
        if (model is null || root is null)
        {
            if (CanPublish(version, ct)) InlayHintsChanged?.Invoke(code, []);
            return;
        }

        var headerLength = HeaderLength(code);
        var result = new List<IInlayHint>();

        foreach (var declaration in root.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            if (declaration.Type is not { IsVar: true } typeSyntax) continue;
            if (declaration.Variables.Count != 1) continue;
            var identifier = declaration.Variables[0].Identifier;
            if (identifier.IsMissing) continue;

            var type = model.GetTypeInfo(typeSyntax, ct).Type;
            AddHint(result, type, identifier.SpanStart, typeSyntax.SpanStart, headerLength, code, model);
        }

        foreach (var forEach in root.DescendantNodes().OfType<ForEachStatementSyntax>())
        {
            if (forEach.Type is not { IsVar: true } typeSyntax) continue;
            if (forEach.Identifier.IsMissing) continue;

            var type = model.GetForEachStatementInfo(forEach).ElementType;
            AddHint(result, type, forEach.Identifier.SpanStart, typeSyntax.SpanStart, headerLength, code, model);
        }

        if (CanPublish(version, ct)) InlayHintsChanged?.Invoke(code, result);

        // Roslyn's own CSharpInlineTypeHintsService (IsValidType) only excludes null and a type
        // literally named "var" (e.g. a user-defined `class var` — legal, if unusual, C#); it doesn't
        // special-case error types beyond that, and neither does this.
        void AddHint(List<IInlayHint> hints, ITypeSymbol? type, int identifierPos, int typeSyntaxPos,
            int headerLength, string code, SemanticModel model)
        {
            if (type is null || type.Name == "var") return;

            var offset = ToUserOffset(code, identifierPos);
            if (offset < 0 || offset > code.Length) return;

            var display = type.ToMinimalDisplayString(model, typeSyntaxPos, _inlayHintTypeFormat);
            hints.Add(new SimpleInlayHint(offset, display + " "));
        }
    }

    // ── Diagnostics + code fixes (real Roslyn CodeFixProviders — see RoslynCodeFixEngine) ──────

    // Combined into one pass — these used to be two separate methods, each independently calling
    // model.GetDiagnostics(...), which forces Roslyn to re-run the full diagnostic/analyzer engine
    // over the document; GetSemanticModelAsync itself is cached per document version, but
    // GetDiagnostics is not, so calling it twice on the same model did the same binding work twice
    // on every 500ms background tick.
    private async Task ComputeDiagnosticsAndCodeFixesAsync(string code, Document document, int version, CancellationToken ct)
    {
        var model = await document.GetSemanticModelAsync(ct);
        if (model == null)
        {
            if (!CanPublish(version, ct)) return;
            DiagnosticsChanged?.Invoke(code, []);
            CodeFixesChanged?.Invoke(code, []);
            return;
        }

        // Roslyn's own IDE-style analyzers ("use var", "make
        // readonly", ...) alongside the compiler's own diagnostics. GetCompilationAsync is the same
        // cached compilation model.GetDiagnostics() already reads from (Roslyn caches per Project
        // version), not a second build.
        var compilation = await document.Project.GetCompilationAsync(ct);
        var analyzerDiagnostics = compilation is null
            ? []
            : await _analyzerEngine.ComputeAsync(compilation, _InvokletAnalyzersEnabled, ct);

        var headerLength = HeaderLength(code);
        var visible = new List<IDiagnosticInfo>();
        var forCodeFixes = new List<Diagnostic>();
        foreach (var d in model.GetDiagnostics(cancellationToken: ct).Concat(analyzerDiagnostics))
        {
            // The analysis workspace is a library to allow incomplete/class-only edits, while
            // scripts are compiled as executables. Its library-only top-level error is artificial.
            if (d.Id == "CS8805") continue;

            // The analyzers see the whole compilation, the generated Connections documents included;
            // offsets there mean nothing in the script, so neither the squiggles nor the code fixes
            // (which map every offset onto the user's text) may take them.
            if (!IsInScript(d.Location)) continue;

            // Unlike compiler diagnostics (never actually Hidden in practice), most of the analyzer
            // diagnostics above fire at Hidden severity BY DESIGN — no squiggle, only a CodeFixProvider
            // keyed to that same diagnostic ID becoming available (RoslynAnalyzerEngine's own doc
            // comment). Still added to forCodeFixes either way — only excluded from the visible list.
            forCodeFixes.Add(d);
            if (d.Severity == RoslynSeverity.Hidden) continue;

            var span = d.Location.SourceSpan;
            var mappedStart = ToUserSpan(code, span);
            if (mappedStart is null) continue;
            var start = mappedStart.Value.Start;

            // Clamp to code.Length before the start>=end skip below, same pattern as
            // ComputeSemanticTokensAsync/ComputeInlayHintsAsync's own bounds checks — needed since
            // MdRunKind.Program's mirrored KindSuffix (the wrapper class's closing brace)
            // now follows the user's own code in the synced document, so a diagnostic anchored there
            // (or straddling into it) needs to not leak an out-of-range span to the editor.
            var end = Math.Min(Math.Max(mappedStart.Value.End, start + 1), code.Length);
            if (start >= end) continue;

            var severity = d.Severity switch
            {
                RoslynSeverity.Error => ContractSeverity.Error,
                RoslynSeverity.Warning => ContractSeverity.Warning,
                _ => ContractSeverity.Info,
            };
            visible.Add(new SimpleDiagnostic(start, end, d.GetMessage(RoslynMessageCulture.Culture), severity));
        }

        // Diagnostics the source generators report themselves (an invalid [GeneratedRegex] pattern, a
        // member System.Text.Json can't see) are part of the build's output but of no semantic model.
        // Only those inside the user's own text are shown; none take part in code fixes.
        if (compilation is not null)
        {
            var seen = new HashSet<(string, int, int)>();
            foreach (var (d, span) in await GeneratorDiagnosticsAsync(document, compilation, model.SyntaxTree, ct))
            {
                if (d.Severity is RoslynSeverity.Hidden or RoslynSeverity.Info) continue;
                var mapped = ToUserSpan(code, span);
                if (mapped is null) continue;
                var start = mapped.Value.Start;
                var end = Math.Min(Math.Max(mapped.Value.End, start + 1), code.Length);
                if (start < 0 || start >= end || !seen.Add((d.Id, start, end))) continue;
                visible.Add(new SimpleDiagnostic(start, end, d.GetMessage(RoslynMessageCulture.Culture),
                    d.Severity == RoslynSeverity.Error ? ContractSeverity.Error : ContractSeverity.Warning));
            }
        }

        if (!CanPublish(version, ct)) return;
        DiagnosticsChanged?.Invoke(code, visible);

        var fixes = await _codeFixEngine.ComputeFixesAsync(document, forCodeFixes, ProjectionFor(code).Text, headerLength, ct);
        if (CanPublish(version, ct)) CodeFixesChanged?.Invoke(code, fixes);
    }

    // The workspace runs the project's generators but keeps what they report to itself, so they are
    // run once more for that, on the compilation without their output: the same instances and parse
    // options, cancelled with the analysis pass. A location is used only when it is in the user's
    // document — generators report at external-file locations naming that document's path — never
    // in generated code, which isn't part of that compilation.
    private static async Task<List<(Diagnostic Diagnostic, TextSpan Span)>> GeneratorDiagnosticsAsync(
        Document document, Compilation compilation, SyntaxTree userTree, CancellationToken ct)
    {
        var generators = document.Project.AnalyzerReferences
            .SelectMany(r => r.GetGenerators(LanguageNames.CSharp)).ToImmutableArray();
        if (generators.IsEmpty) return [];

        var generated = await document.Project.GetSourceGeneratedDocumentsAsync(ct);
        var generatedTrees = new List<SyntaxTree>();
        foreach (var g in generated)
            generatedTrees.Add(await g.GetSyntaxTreeAsync(ct) ?? throw new InvalidOperationException());
        var withoutGenerated = compilation.RemoveSyntaxTrees(generatedTrees);

        var run = CSharpGeneratorDriver
            .Create(generators, parseOptions: (CSharpParseOptions?)document.Project.ParseOptions)
            .RunGenerators(withoutGenerated, ct)
            .GetRunResult();

        var result = new List<(Diagnostic, TextSpan)>();
        foreach (var d in run.Diagnostics)
        {
            var location = d.Location;
            var inUserTree = location.IsInSource
                ? location.SourceTree == userTree
                : location.Kind == LocationKind.ExternalFile && location.GetLineSpan().Path == userTree.FilePath;
            if (inUserTree && location.SourceSpan.End <= userTree.Length)
                result.Add((d, location.SourceSpan));
        }
        return result;
    }

    private static SemanticTokenKind ClassifySpan(string type) => type switch
    {
        ClassificationTypeNames.ClassName or "record class name" => SemanticTokenKind.ClassName,
        ClassificationTypeNames.StructName or "record struct name" => SemanticTokenKind.StructName,
        ClassificationTypeNames.InterfaceName => SemanticTokenKind.InterfaceName,
        ClassificationTypeNames.EnumName => SemanticTokenKind.EnumName,
        ClassificationTypeNames.DelegateName => SemanticTokenKind.DelegateName,
        ClassificationTypeNames.TypeParameterName => SemanticTokenKind.TypeParameterName,
        ClassificationTypeNames.MethodName => SemanticTokenKind.MethodName,
        ClassificationTypeNames.ExtensionMethodName => SemanticTokenKind.ExtensionMethodName,
        ClassificationTypeNames.PropertyName => SemanticTokenKind.PropertyName,
        ClassificationTypeNames.FieldName or "constant name" => SemanticTokenKind.FieldName,
        ClassificationTypeNames.EventName => SemanticTokenKind.EventName,
        ClassificationTypeNames.NamespaceName => SemanticTokenKind.NamespaceName,
        ClassificationTypeNames.LocalName => SemanticTokenKind.LocalName,
        ClassificationTypeNames.ParameterName => SemanticTokenKind.ParameterName,
        _ => SemanticTokenKind.None,
    };

    // ── Package references ────────────────────────────────────────────────────

    // The build's own reading of the request (see DotnetPackageReferenceContext). Without an SDK
    // yet, the runtime IntelliSense itself runs on.
    private string? _connectionContractReference;
    private GeneratedConnections? _requestedConnections;
    private string? _requestedContractAssembly;
    private bool _contractSourcesNeeded;
    private int _contextRequests;
    private int _appliedContextRequest; // under _syncLock

    private void ApplyConnectionRepresentation()
    {
        var contract = _contractSourcesNeeded ? null : _requestedContractAssembly;
        UpdateConnections(_requestedConnections is { Source.Length: > 0 } connections
            ? [new GeneratedSourceFile("Connections.g.cs", connections.Source), .. contract is null ? connections.Files : []]
            : [], contract);
    }

    public void UpdateExecutionContext(ExecutionRequest request) => ApplyExecutionContext(request, CancellationToken.None);

    // A request cancelled before it is applied changes nothing: the slow parts (reading the NuGet
    // assets, the targeting pack, the contract assembly lookup) run without the workspace lock, and
    // a caller that has since asked again must not have its newer context overwritten when this one
    // finally finishes.
    //
    // Requests also supersede each other by the order they were made in: one that finishes after a
    // later request has been applied is dropped, whether or not its caller cancelled it.
    public void ApplyExecutionContext(ExecutionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sequence = Interlocked.Increment(ref _contextRequests);
        var packages = request.Packages.ToDictionary(p => p.Key, p => new Package(p.Value));
        var context = DotnetPackageReferenceContext.For(request);
        if (context is not null) ApplyPackageReferences(packages, context, cancellationToken);
        else ApplyPackageReferences(packages, new PackageReferenceContext($"net{Environment.Version.Major}.{Environment.Version.Minor}", null, null), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        string? contract = null;
        if (request.Connections is { Files.Count: > 0 } generated && context?.OutputDirectory is { } output)
        {
            var (framework, packagePaths) = RoslynScriptCompiler.ResolveConflicts(_frameworkPaths, _nugetPaths);
            try
            {
                contract = ConnectionContractAssemblyCache.Find(RoslynScriptCompiler.ObjDirectory(output), generated.Files,
                    framework.Concat(packagePaths), context.TargetFramework + ";" + (request.AllowUnsafe ? "unsafe" : ""));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "No cached connection contract available");
            }
        }
        ExecutionContextResolvedForTests?.Invoke(request);
        lock (_syncLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sequence < _appliedContextRequest) return;
            _appliedContextRequest = sequence;
            UpdateAllowUnsafe(request.AllowUnsafe);
            _requestedConnections = request.Connections;
            _requestedContractAssembly = contract;
            ApplyConnectionRepresentation();
        }
    }

    // A source location in the script's own document, not in the generated Connections document,
    // whose offsets mean nothing in the editor.
    private bool IsInScript(Location location) =>
        location.IsInSource && location.SourceTree is { } tree && _workspace.CurrentSolution.GetDocumentId(tree) == _documentId;

    // A symbol declared in one of the generated connection documents (UpdateConnections) rather
    // than the script itself or a compiled metadata assembly -- the state every gRPC/HTTP-contract
    // message and client class is in until the first successful build produces a cached contract
    // assembly (ConnectionContractAssemblyCache). Neither IsInScript nor a metadata reference
    // matches a symbol there, so without this, TryGetDefinitionAsync falls through and F12 is a
    // silent no-op (see handoffs/g03-navigation-cold-start-gap.md for how this was found).
    private (string Text, int Line)? GeneratedDocumentFor(ISymbol symbol)
    {
        foreach (var location in symbol.Locations)
        {
            if (!location.IsInSource || location.SourceTree is not { } tree) continue;
            var id = _workspace.CurrentSolution.GetDocumentId(tree);
            if (id is null) continue;
            foreach (var (_, (docId, text)) in _connectionFiles)
                if (docId == id)
                    return (text, location.GetLineSpan().StartLinePosition.Line + 1);
        }
        return null;
    }

    private const string GeneratedHeader = "// <auto-generated/>\n";

    // The small Connections wrapper stays source. Schema types use the build's cached assembly
    // when available; before the first build (or for partial extensions) they remain source documents.
    private void UpdateConnections(IReadOnlyList<GeneratedSourceFile> files, string? contract)
    {
        lock (_syncLock)
        {
            var project = _workspace.CurrentSolution.GetProject(_documentId.ProjectId)!;
            var filesChanged = !files.Select(f => (f.Name, f.Text)).SequenceEqual(_connectionFiles.Select(f => (f.Key, f.Value.Text)));
            var referencesChanged = contract != _connectionContractReference
                || contract is not null && !project.MetadataReferences.Any(r => r.Display == contract);
            if (!filesChanged && !referencesChanged) return;
            var solution = project.Solution;
            if (referencesChanged)
            {
                var references = project.MetadataReferences.Where(r => r.Display != _connectionContractReference).ToList();
                if (contract is not null) references.Add(CreateReference(contract));
                solution = project.WithMetadataReferences(references).Solution;
            }
            var added = _connectionFiles;
            if (filesChanged)
            {
                foreach (var id in _connectionFiles.Select(f => f.Value.Id)) solution = solution.RemoveDocument(id);
                added = [];
                foreach (var file in files)
                {
                    var id = DocumentId.CreateNewId(_documentId.ProjectId, file.Name);
                    solution = solution.AddDocument(id, file.Name, SourceText.From(GeneratedHeader + file.Text, Encoding.UTF8));
                    added.Add(KeyValuePair.Create(file.Name, (id, file.Text)));
                }
            }
            // A single solution change: analysis must never see both source and metadata definitions.
            if (!_workspace.TryApplyChanges(solution)) return;
            _connectionFiles = added;
            _connectionContractReference = contract;
            _lastCode = "";
            _contextVersion++;
        }
    }

    // The same /unsafe setting the build uses, so the editor neither flags allowed unsafe code (CS0227)
    // nor hides that a script without the setting won't compile.
    private void UpdateAllowUnsafe(bool allowUnsafe)
    {
        lock (_syncLock)
        {
            var project = _workspace.CurrentSolution.GetProject(_documentId.ProjectId)!;
            var options = (CSharpCompilationOptions)project.CompilationOptions!;
            if (options.AllowUnsafe == allowUnsafe) return;
            if (!_workspace.TryApplyChanges(project.WithCompilationOptions(options.WithAllowUnsafe(allowUnsafe)).Solution)) return;
            _lastCode = ""; // force re-sync on next analysis pass
            _contextVersion++;
        }
    }

    // Without a build context: the runtime IntelliSense itself runs on, the user's NuGet settings.
    public void UpdatePackageReferences(IReadOnlyDictionary<string, Package> packages)
        => UpdatePackageReferences(packages,
            new PackageReferenceContext($"net{Environment.Version.Major}.{Environment.Version.Minor}", null, null));

    // The same assemblies a build compiles against: the target framework's reference assemblies, and the
    // last restore's selection while it still matches the packages (resolved versions, transitive
    // packages, the target framework's compile assets), else the best local match by NuGet's version
    // rules — never a restore or a targeting pack download, which would need the network.
    public void UpdatePackageReferences(IReadOnlyDictionary<string, Package> packages, PackageReferenceContext context)
        => ApplyPackageReferences(packages, context, CancellationToken.None);

    private void ApplyPackageReferences(IReadOnlyDictionary<string, Package> packages, PackageReferenceContext context,
        CancellationToken cancellationToken)
    {
        var (frameworkPaths, generatorPaths) = TargetFrameworkReferences(context);
        var userReferencePaths = RoslynScriptCompiler.WithSameFolderDependencies(context.AssemblyReferences ?? []);
        var parseOptions = TargetFramework.TryParse(context.TargetFramework, out var major, out var minor)
            ? CSharpScriptCompiler.ParseOptions(major, minor)
            : CSharpScriptCompiler.ParseOptions(Environment.Version.Major, Environment.Version.Minor);
        var newPaths = NuGetRestorer.CompileReferencesWithoutRestore(
                packages.ToDictionary(p => p.Key, p => p.Value.Version, StringComparer.OrdinalIgnoreCase),
                context.TargetFramework,
                context.OutputDirectory is { } output ? RoslynScriptCompiler.ObjDirectory(output) : null,
                context.NuGetConfigFile)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The same workspace lock as SyncDocument: a background analysis pass applies its own changes
        // under it, and both sides must see one consistent "current" solution.
        lock (_syncLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (newPaths.SequenceEqual(_nugetPaths, StringComparer.OrdinalIgnoreCase)
                && frameworkPaths.SequenceEqual(_frameworkPaths, StringComparer.OrdinalIgnoreCase)
                && parseOptions.Equals(_parseOptions)
                && generatorPaths.SequenceEqual(_generatorPaths, StringComparer.OrdinalIgnoreCase)
                && userReferencePaths.SequenceEqual(_userReferencePaths, StringComparer.OrdinalIgnoreCase)) return;

            // Rebuilt from the base references every time, so stale package ones (a removed package, an
            // older resolved version) go away. A package assembly the framework also has is resolved as
            // the build resolves it: referencing both would make every one of its types ambiguous
            // (CS0433) in the editor while the build compiles.
            var (basePaths, packagePaths) = RoslynScriptCompiler.ResolveConflicts(
                frameworkPaths.Concat(_otherBaseReferencePaths), newPaths);
            // In the build's order: the framework (and Invoklet.Runtime), the script's own assemblies, packages.
            var baseRefs = basePaths.Concat(userReferencePaths).Select(p => (MetadataReference)CreateReference(p)).ToList();
            var newRefs = packagePaths.Select(p => (MetadataReference)CreateReference(p)).ToList();

            // Replaced as a whole: another target's generators never linger next to the new ones.
            var generatorRefs = generatorPaths.Select(p => (AnalyzerReference)new GeneratorReference(p, _logger)).ToList();

            var project = _workspace.CurrentSolution.GetProject(_documentId.ProjectId)!;

            if (!_workspace.TryApplyChanges(project.WithMetadataReferences(baseRefs.Concat(newRefs).ToList())
                    .WithParseOptions(parseOptions).WithAnalyzerReferences(generatorRefs).Solution))
                return; // the next update tries again: _nugetPaths still describes the workspace

            _nugetRefs.Clear();
            _nugetRefs.AddRange(newRefs);
            _nugetPaths = newPaths;
            _frameworkPaths = frameworkPaths;
            _parseOptions = parseOptions;
            _generatorPaths = generatorPaths;
            _userReferencePaths = userReferencePaths;
            // force re-sync on next analysis pass
            _lastCode = "";
            _contextVersion++;
        }
    }

    // The targeting pack the build would use for the target, among the installed and already downloaded
    // ones: its reference assemblies and C# source generators. Without one (or with a malformed NuGet
    // configuration), the references the service was created with and no generators, as before.
    private (IReadOnlyList<string> References, IReadOnlyList<string> Generators) TargetFrameworkReferences(PackageReferenceContext context)
    {
        try
        {
            var settings = NuGetRestorer.LoadSettings(context.NuGetConfigFile,
                context.OutputDirectory is { } output ? RoslynScriptCompiler.ObjDirectory(output) : Path.GetTempPath());
            var pack = TargetingPack.Find(context.TargetFramework, context.DotnetExecutable,
                SettingsUtility.GetGlobalPackagesFolder(settings));
            if (pack is not null)
                return (pack.ReferenceAssemblies.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                    pack.GeneratorAssemblies("cs").Order(StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NuGetConfigurationException)
        {
            _logger.LogDebug(ex, "No targeting pack for {TargetFramework}", context.TargetFramework);
        }
        return (_defaultFrameworkPaths, []);
    }

    // Exposes only the source generators of an assembly: the analyzers and code fixes that ship next to
    // them are not part of the build's diagnostics either. Loaded when the workspace first asks, while
    // compiling for analysis — not on the thread that changes the target. The path is the identity, so
    // an unchanged generator is recognized by the workspace across updates.
    private sealed class GeneratorReference(string path, ILogger logger) : AnalyzerReference
    {
        public override string FullPath => path;
        public override string Display => Path.GetFileNameWithoutExtension(path);
        public override object Id => path;
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];
        public override ImmutableArray<ISourceGenerator> GetGenerators(string language) =>
            language == LanguageNames.CSharp ? GetGeneratorsForAllLanguages() : [];
        public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() =>
            RoslynScriptCompiler.LoadGenerators(path, LanguageNames.CSharp, logger);
    }

    // Test-only (InternalsVisibleTo): the package assemblies resolved for the packages — the workspace
    // references them unless the framework provides the same assembly at a higher version.
    internal IReadOnlyList<string> PackageReferencePathsForTests
    {
        get { lock (_syncLock) return _nugetPaths; }
    }

    // Test-only (InternalsVisibleTo): the source generator assemblies the workspace runs.
    internal IReadOnlyList<string> GeneratorPathsForTests
    {
        get
        {
            lock (_syncLock)
                return _workspace.CurrentSolution.GetProject(_documentId.ProjectId)!.AnalyzerReferences
                    .Select(r => r.FullPath!).ToList();
        }
    }

    // Test-only (InternalsVisibleTo): every assembly the workspace currently references.
    internal IReadOnlyList<string> ReferencePathsForTests
    {
        get
        {
            lock (_syncLock)
                return _workspace.CurrentSolution.GetProject(_documentId.ProjectId)!.MetadataReferences
                    .OfType<PortableExecutableReference>().Select(r => r.FilePath!).ToList();
        }
    }

    // Cancel() alone only *requests* cooperative cancellation — NotifyTextChanged's Task.Run body can
    // still be mid-flight (e.g. deep inside RoslynCodeFixEngine.ComputeFixesAsync, which doesn't poll
    // the token between individual CodeFixProviders) when this method would otherwise return
    // immediately, racing _workspace.Dispose() right below and continuing to burn CPU on a ThreadPool
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
        _diagnosticsCts?.Cancel();
        try { _backgroundAnalysisTask?.Wait(_backgroundAnalysisDisposeTimeout); }
        catch { /* already logged from inside the task itself */ }
        _diagnosticsCts?.Dispose();
        _workspace.Dispose();
    }

    // ── Private model types ────────────────────────────────────────────────────

    private sealed class SimpleSignatureHelpResult(
        IReadOnlyList<ISignatureHelpItem> items, int selectedIndex, int argumentIndex, bool isArgumentSlotEmpty)
        : ISignatureHelpResult
    {
        public IReadOnlyList<ISignatureHelpItem> Items => items;
        public int SelectedItemIndex => selectedIndex;
        public int ArgumentIndex => argumentIndex;
        public bool IsArgumentSlotEmpty => isArgumentSlotEmpty;
    }

    private sealed class SimpleSignatureHelpItem : ISignatureHelpItem
    {
        // activeParameter is bolded in the header, the way LINQPad/VS Code do.
        public SimpleSignatureHelpItem(IMethodSymbol method, IParameterSymbol? activeParameter)
        {
            Signature = method.ToDisplayParts(_briefFormat).ToSegments(activeParameter);
            Summary = XmlDocSummary.GetSummary(method);
            Parameters = method.Parameters
                .Select(p => (ISignatureHelpParameter)new SimpleSignatureHelpParameter(method, p))
                .ToList();
            ActiveParameterIndex = activeParameter is null ? -1 : method.Parameters.IndexOf(activeParameter);
        }

        public IReadOnlyList<DisplayTextSegment> Signature { get; }
        public string? Summary { get; }
        public IReadOnlyList<ISignatureHelpParameter> Parameters { get; }
        public int ActiveParameterIndex { get; }
    }

    private sealed class SimpleSignatureHelpParameter : ISignatureHelpParameter
    {
        public SimpleSignatureHelpParameter(IMethodSymbol method, IParameterSymbol parameter)
        {
            Name = parameter.Name;
            Signature = parameter.ToDisplayParts(_briefFormat).ToSegments();
            Summary = XmlDocSummary.GetParameterSummary(method, parameter.Name);
        }

        public string Name { get; }
        public IReadOnlyList<DisplayTextSegment> Signature { get; }
        public string? Summary { get; }
    }

    private sealed record SimpleQuickInfoResult(
        IReadOnlyList<DisplayTextSegment> Signature, string? Summary) : IQuickInfoResult;

    private sealed record SimpleDocumentHighlightSpan(int Start, int Length) : IDocumentHighlightSpan;

    private sealed record SimpleRenameInfo(
        string CurrentName, IReadOnlyList<IDocumentHighlightSpan> Spans) : IRenameInfo;

    private sealed record SimpleDefinitionResult(
        bool IsInSource, int Offset, string? AssemblyPath, string? ContainingTypeFullName,
        string? MemberName, IReadOnlyList<string> ParameterTypeNames, string DisplayName,
        (string Text, int Line)? GeneratedDocument = null) : IDefinitionResult;

    private sealed record SimpleImplementationLocation(int Offset, string DisplayName) : IImplementationLocation;

    private sealed record SimpleDiagnostic(
        int StartOffset, int EndOffset, string Message,
        ContractSeverity Severity) : IDiagnosticInfo;

    private sealed record SimpleSemanticToken(
        int StartOffset, int Length, SemanticTokenKind Kind) : ISemanticToken;

    private sealed record SimpleInlayHint(int Offset, string Text) : IInlayHint;

    private sealed record SimpleMaterializedFix(string NewCode, IReadOnlyList<string> UsingsToAdd) : IMaterializedFix;

}
