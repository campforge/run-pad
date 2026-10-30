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

/// <summary>
/// One background pass over the text of the script: the diagnostics with their code fixes, the semantic tokens and the inlay hints,
/// published through events only while the project's context is still the one the pass began on and no newer text has cancelled it.
/// </summary>
internal sealed class BackgroundAnalysis(
    ScriptDocumentSession session, Func<string, Document> syncDocument, ScriptDocumentLayout layout, ScriptContext context,
    RoslynCodeFixEngine codeFixEngine, RoslynAnalyzerEngine analyzerEngine, Func<bool> analyzersEnabled)
{
    private readonly ScriptDocumentSession _session = session;
    private readonly Func<string, Document> _syncDocument = syncDocument;
    private readonly ScriptDocumentLayout _layout = layout;
    private readonly ScriptContext _context = context;
    private readonly RoslynCodeFixEngine _codeFixEngine = codeFixEngine;
    private readonly RoslynAnalyzerEngine _analyzerEngine = analyzerEngine;
    private readonly Func<bool> _analyzersEnabled = analyzersEnabled;

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

    private volatile bool _stopped;

    /// <summary>After this nothing is published, whatever a pass that outlives its owner still finds.</summary>
    internal void Stop() => _stopped = true;

    // Test-only: called once a pass holds its document snapshot, before it analyses it, so a test can change the context at exactly that point.
    internal Action? SnapshotTakenForTests { get; set; }

    public event Action<string, IReadOnlyList<IDiagnosticInfo>>? DiagnosticsChanged;
    public event Action<string, IReadOnlyList<ISemanticToken>>? SemanticTokensChanged;
    public event Action<string, IReadOnlyList<ICodeFixInfo>>? CodeFixesChanged;
    public event Action<string, IReadOnlyList<IInlayHint>>? InlayHintsChanged;

    // One background pass over the text; false when the project context changed meanwhile, which makes what it found describe the
    // old references or generated classes (it was not published): the scheduler runs it again on the new solution, unless a newer
    // notification has taken over already.
    internal async Task<bool> AnalyzeAsync(string code, CancellationToken ct)
    {
        Document document;
        int version;
        lock (_session.SyncLock)
        {
            document = _syncDocument(code);
            version = _session.ContextVersion;
        }
        SnapshotTakenForTests?.Invoke();
        await Task.WhenAll(
            ComputeDiagnosticsAndCodeFixesAsync(code, document, version, ct),
            ComputeSemanticTokensAsync(code, document, version, ct),
            ComputeInlayHintsAsync(code, document, version, ct));
        return IsCurrent(version);
    }

    private bool IsCurrent(int version) => _session.IsCurrent(version);

    // Whether a result may be published: a newer text notification cancels the pass, and a change to
    // the project's context makes its solution obsolete. Checked right before each event, since the
    // analysis itself may finish after either.
    private bool CanPublish(int version, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_stopped) return false;
        return IsCurrent(version);
    }

    private async Task ComputeSemanticTokensAsync(string code, Document document, int version, CancellationToken ct)
    {
        var headerLength = _layout.HeaderLength(code);
        var textLen = headerLength + _layout.Projection(code).Text.Length;
        var spans = await Classifier.GetClassifiedSpansAsync(
            document, new TextSpan(0, textLen), ct);

        var result = new List<ISemanticToken>();
        foreach (var span in spans)
        {
            var kind = ClassifySpan(span.ClassificationType);
            if (kind == SemanticTokenKind.None) continue;

            var mapped = _layout.ToUserSpan(code, span.TextSpan);
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

        var headerLength = _layout.HeaderLength(code);
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

            var offset = _layout.ToUserOffset(code, identifierPos);
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
            : await _analyzerEngine.ComputeAsync(compilation, _analyzersEnabled(), ct);

        var headerLength = _layout.HeaderLength(code);
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
            if (!_context.IsInScript(d.Location)) continue;

            // Unlike compiler diagnostics (never actually Hidden in practice), most of the analyzer
            // diagnostics above fire at Hidden severity BY DESIGN — no squiggle, only a CodeFixProvider
            // keyed to that same diagnostic ID becoming available (RoslynAnalyzerEngine's own doc
            // comment). Still added to forCodeFixes either way — only excluded from the visible list.
            forCodeFixes.Add(d);
            if (d.Severity == RoslynSeverity.Hidden) continue;

            var span = d.Location.SourceSpan;
            var mappedStart = _layout.ToUserSpan(code, span);
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
                var mapped = _layout.ToUserSpan(code, span);
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

        var fixes = await _codeFixEngine.ComputeFixesAsync(document, forCodeFixes, _layout.Projection(code).Text, headerLength, ct);
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

    private sealed record SimpleDiagnostic(
        int StartOffset, int EndOffset, string Message,
        ContractSeverity Severity) : IDiagnosticInfo;

    private sealed record SimpleSemanticToken(
        int StartOffset, int Length, SemanticTokenKind Kind) : ISemanticToken;

    private sealed record SimpleInlayHint(int Offset, string Text) : IInlayHint;
}
