using System.Text;
using DotPad.Contracts.IntelliSense;
using DotPad.Format.RPad;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using NuGet.Configuration;
using ContractSeverity = DotPad.Contracts.IntelliSense.DiagnosticSeverity;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace DotPad.Languages.CSharp.IntelliSense;

public sealed class CSharpIntelliSenseService : IIntelliSenseService
{
    // Default usings the synthetic script document is built with, so completion/diagnostics work
    // without the user typing them — must match the compiler's own implicit usings exactly (see
    // ImplicitUsings.Default / ANALYSIS.md #27), otherwise IntelliSense shows no error for something
    // that won't actually compile. Extended at runtime with the .rpad front matter's own `usings:`
    // list (see UpdateGlobalUsings) so IntelliSense agrees with what the compiler will actually add
    // as `global using` too — see docs/ImplementationPlanRPad.md and RoslynCodeFixEngine.
    private static readonly string[] _basePreambleUsings = ImplicitUsings.Default;

    private static readonly SymbolDisplayFormat _briefFormat =
        new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

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
        }
        .Select(kw => (ICompletionItem)new SimpleCompletionItem(kw, CompletionItemKind.Keyword, null))
        .ToArray();

    private static readonly string[] _tfmPreference =
    [
        "net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0",
        "netcoreapp3.1", "netstandard2.1", "netstandard2.0",
    ];

    private readonly ILogger<CSharpIntelliSenseService> _logger;
    private readonly AdhocWorkspace _workspace;
    private readonly DocumentId _documentId;
    private readonly string _nugetRoot = ResolveNuGetRoot();
    private readonly List<MetadataReference> _nugetRefs = [];
    private readonly RoslynCodeFixEngine _codeFixEngine;
    private string[] _extraPreambleUsings = [];
    private string _preamble = "";
    private int _preambleLength;
    private string _lastCode = "";
    private CancellationTokenSource? _diagnosticsCts;

    public CSharpIntelliSenseService(IEnumerable<string> referencePaths, ILogger<CSharpIntelliSenseService> logger)
    {
        _logger = logger;
        RebuildPreamble();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>();
        foreach (var path in referencePaths)
            if (File.Exists(path) && seen.Add(path))
                refs.Add(MetadataReference.CreateFromFile(path));

        _workspace = new AdhocWorkspace(DotPadRoslynHost.HostServices);
        _codeFixEngine = new RoslynCodeFixEngine(DotPadRoslynHost.CompositionHost, logger);

        var projectId = ProjectId.CreateNewId("DotPadScript");
        _documentId = DocumentId.CreateNewId(projectId, "Script.cs");

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name: "DotPadScript",
            assemblyName: "DotPadScript",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: refs);

        _workspace.AddProject(projectInfo);
        _workspace.AddDocument(DocumentInfo.Create(
            _documentId,
            "Script.cs",
            loader: TextLoader.From(TextAndVersion.Create(
                SourceText.From(_preamble, Encoding.UTF8),
                VersionStamp.Create()))));
    }

    private void RebuildPreamble()
    {
        var sb = new StringBuilder();
        foreach (var u in _basePreambleUsings) sb.Append("using ").Append(u).Append(";\n");
        foreach (var u in _extraPreambleUsings) sb.Append("using ").Append(u).Append(";\n");
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
        _lastCode = "";  // force re-sync with the new preamble on the next analysis pass
    }

    // ── Document sync ──────────────────────────────────────────────────────────

    private Document SyncDocument(string code)
    {
        if (_lastCode != code)
        {
            _lastCode = code;
            var text = SourceText.From(_preamble + code, Encoding.UTF8);
            var newSolution = _workspace.CurrentSolution.WithDocumentText(_documentId, text);
            _workspace.TryApplyChanges(newSolution);
        }
        return _workspace.CurrentSolution.GetDocument(_documentId)!;
    }

    // ── Completion ─────────────────────────────────────────────────────────────

    public Task<ICompletionList?> GetCompletionsAsync(
        string code, int position, CompletionTrigger trigger = default, CancellationToken ct = default)
        => Task.Run(async () => (ICompletionList?)await TryGetCompletionsAsync(code, position, trigger, ct), ct);

    private async Task<ICompletionList?> TryGetCompletionsAsync(
        string code, int position, CompletionTrigger trigger, CancellationToken ct)
    {
        try
        {
            var document = SyncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = position + _preambleLength;

            if (trigger.Kind == CompletionTriggerKind.Insertion && trigger.Character == '.')
            {
                if (pos <= 0) return null;
                var token = root.FindToken(pos - 1);
                var memberAccess = token.Parent?.AncestorsAndSelf()
                    .OfType<MemberAccessExpressionSyntax>()
                    .FirstOrDefault();
                if (memberAccess == null) return null;

                var typeInfo = model.GetTypeInfo(memberAccess.Expression);
                var type = typeInfo.Type ?? typeInfo.ConvertedType;
                if (type == null) return null;

                var symbols = model.LookupSymbols(pos, container: type, includeReducedExtensionMethods: true);
                var items = symbols
                    .Select(ToItem)
                    .Where(i => i != null)
                    .Cast<ICompletionItem>()
                    .GroupBy(i => i.DisplayText)
                    .Select(g => g.OrderBy(i => (int)i.Kind).First())
                    .OrderBy(i => i.SortText)
                    .ToList();

                return items.Count > 0 ? new SimpleCompletionList(items) : null;
            }
            else
            {
                var symbols = model.LookupSymbols(pos);
                var items = symbols
                    .Select(ToItem)
                    .Where(i => i != null)
                    .Cast<ICompletionItem>()
                    .Concat(_keywords)
                    .GroupBy(i => i.DisplayText)
                    .Select(g => g.OrderBy(i => (int)i.Kind).First())
                    .OrderBy(i => i.SortText)
                    .ToList();

                return items.Count > 0 ? new SimpleCompletionList(items) : null;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Completions failed"); return null; }
    }

    private static ICompletionItem? ToItem(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m when m.MethodKind is MethodKind.Ordinary or MethodKind.ReducedExtension
            => new SimpleCompletionItem(m.Name, CompletionItemKind.Method, m.ToDisplayString(_briefFormat)),
        IPropertySymbol p
            => new SimpleCompletionItem(p.Name, CompletionItemKind.Property, p.ToDisplayString(_briefFormat)),
        IFieldSymbol f when !f.IsImplicitlyDeclared
            => new SimpleCompletionItem(f.Name, CompletionItemKind.Field, f.ToDisplayString(_briefFormat)),
        IEventSymbol e
            => new SimpleCompletionItem(e.Name, CompletionItemKind.Event, e.ToDisplayString(_briefFormat)),
        ILocalSymbol l
            => new SimpleCompletionItem(l.Name, CompletionItemKind.Variable, l.ToDisplayString(_briefFormat)),
        IParameterSymbol p
            => new SimpleCompletionItem(p.Name, CompletionItemKind.Variable, p.ToDisplayString(_briefFormat)),
        INamedTypeSymbol t
            => new SimpleCompletionItem(t.Name,
                t.TypeKind == TypeKind.Interface ? CompletionItemKind.Interface : CompletionItemKind.Class,
                t.ToDisplayString(_briefFormat)),
        _ => null
    };

    // ── Signature Help ─────────────────────────────────────────────────────────

    public Task<ISignatureHelpResult?> GetSignatureHelpAsync(
        string code, int position, char? triggerCharacter = null, CancellationToken ct = default)
        => Task.Run(async () => (ISignatureHelpResult?)await TryGetSignatureHelpAsync(code, position, ct), ct);

    private async Task<ISignatureHelpResult?> TryGetSignatureHelpAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            var document = SyncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = position + _preambleLength;
            var node = root.FindNode(new TextSpan(Math.Max(0, pos - 1), 1), getInnermostNodeForTie: true);
            var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            if (invocation == null) return null;

            var argIndex = CountArgumentsBefore(invocation.ArgumentList, pos);
            var argCount = invocation.ArgumentList.Arguments.Count;

            var symInfo = model.GetSymbolInfo(invocation);
            var methods = (symInfo.Symbol is IMethodSymbol s ? [s] : Array.Empty<IMethodSymbol>())
                .Concat(symInfo.CandidateSymbols.OfType<IMethodSymbol>())
                .ToList();

            if (methods.Count == 0 && invocation.Expression != null)
                methods = model.GetMemberGroup(invocation.Expression).OfType<IMethodSymbol>().ToList();

            if (methods.Count == 0) return null;

            var items = methods.Select(m => (ISignatureHelpItem)new SimpleSignatureHelpItem(m)).ToList();
            var selectedIndex = methods.FindIndex(m => m.Parameters.Length >= argCount);
            if (selectedIndex < 0) selectedIndex = methods.Count - 1;

            return new SimpleSignatureHelpResult(items, selectedIndex, argIndex);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Signature help failed"); return null; }
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

    // ── Quick Info ─────────────────────────────────────────────────────────────

    public Task<IQuickInfoResult?> GetQuickInfoAsync(
        string code, int position, CancellationToken ct = default)
        => Task.Run(async () => (IQuickInfoResult?)await TryGetQuickInfoAsync(code, position, ct), ct);

    private async Task<IQuickInfoResult?> TryGetQuickInfoAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            var document = SyncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = position + _preambleLength;
            var token = root.FindToken(pos);
            if (token == default) return null;

            var symInfo = model.GetSymbolInfo(token.Parent!);
            var symbol = symInfo.Symbol ?? symInfo.CandidateSymbols.FirstOrDefault()
                ?? (ISymbol?)model.GetTypeInfo(token.Parent!).Type;

            if (symbol == null) return null;

            return new SimpleQuickInfoResult(symbol.ToDisplayString(_briefFormat));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Quick info failed"); return null; }
    }

    // ── Background analysis (diagnostics + semantic tokens) ───────────────────

    public event Action<string, IReadOnlyList<IDiagnosticInfo>>? DiagnosticsChanged;
    public event Action<string, IReadOnlyList<ISemanticToken>>? SemanticTokensChanged;
    public event Action<string, IReadOnlyList<ICodeFixInfo>>? CodeFixesChanged;

    public void NotifyTextChanged(string code)
    {
        _diagnosticsCts?.Cancel();
        _diagnosticsCts = new CancellationTokenSource();
        var ct = _diagnosticsCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, ct);
                var document = SyncDocument(code);
                await Task.WhenAll(
                    ComputeDiagnosticsAsync(code, document, ct),
                    ComputeSemanticTokensAsync(code, document, ct),
                    ComputeCodeFixesAsync(code, document, ct));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Background analysis failed"); }
        }, CancellationToken.None);
    }

    private async Task ComputeDiagnosticsAsync(string code, Document document, CancellationToken ct)
    {
        var model = await document.GetSemanticModelAsync(ct);
        if (model == null) { DiagnosticsChanged?.Invoke(code, []); return; }

        var result = new List<IDiagnosticInfo>();
        foreach (var d in model.GetDiagnostics(cancellationToken: ct))
        {
            if (d.Severity == RoslynSeverity.Hidden) continue;
            if (!d.Location.IsInSource) continue;

            var span = d.Location.SourceSpan;
            var start = span.Start - _preambleLength;
            if (start < 0) continue;

            var end = Math.Max(span.End - _preambleLength, start + 1);
            var severity = d.Severity switch
            {
                RoslynSeverity.Error => ContractSeverity.Error,
                RoslynSeverity.Warning => ContractSeverity.Warning,
                _ => ContractSeverity.Info,
            };
            result.Add(new SimpleDiagnostic(start, end, d.GetMessage(), severity));
        }

        DiagnosticsChanged?.Invoke(code, result);
    }

    private async Task ComputeSemanticTokensAsync(string code, Document document, CancellationToken ct)
    {
        var textLen = _preambleLength + code.Length;
        var spans = await Classifier.GetClassifiedSpansAsync(
            document, new TextSpan(0, textLen), ct);

        var result = new List<ISemanticToken>();
        foreach (var span in spans)
        {
            var kind = ClassifySpan(span.ClassificationType);
            if (kind == SemanticTokenKind.None) continue;

            var start = span.TextSpan.Start - _preambleLength;
            if (start < 0) continue;
            var end = Math.Min(span.TextSpan.End - _preambleLength, code.Length);
            if (start >= end) continue;

            result.Add(new SimpleSemanticToken(start, end - start, kind));
        }

        SemanticTokensChanged?.Invoke(code, result);
    }

    // ── Code fixes (real Roslyn CodeFixProviders — see RoslynCodeFixEngine) ────

    private async Task ComputeCodeFixesAsync(string code, Document document, CancellationToken ct)
    {
        var model = await document.GetSemanticModelAsync(ct);
        if (model == null) { CodeFixesChanged?.Invoke(code, []); return; }

        var diagnostics = model.GetDiagnostics(cancellationToken: ct)
            .Where(d => d.Severity != RoslynSeverity.Hidden)
            .ToList();

        var result = await _codeFixEngine.ComputeFixesAsync(document, diagnostics, code, _preambleLength, ct);
        CodeFixesChanged?.Invoke(code, result);
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

    public void UpdatePackageReferences(IReadOnlyDictionary<string, Package> packages)
    {
        var newPaths = packages
            .SelectMany(kv => FindPackageDlls(_nugetRoot, kv.Key, kv.Value.Version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var newRefs = newPaths
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var project = _workspace.CurrentSolution.GetProject(_documentId.ProjectId)!;

        // Keep base BCL refs, replace NuGet refs with the new set.
        var baseRefs = project.MetadataReferences
            .Where(r => !_nugetRefs.Contains(r))
            .ToList();

        _nugetRefs.Clear();
        _nugetRefs.AddRange(newRefs);

        _workspace.TryApplyChanges(
            project.WithMetadataReferences(baseRefs.Concat(newRefs).ToList()).Solution);
        _lastCode = "";  // force re-sync on next analysis pass
    }

    private static IEnumerable<string> FindPackageDlls(string nugetRoot, string id, string version)
    {
        var libDir = Path.Combine(nugetRoot, id.ToLowerInvariant(), version, "lib");
        if (!Directory.Exists(libDir)) yield break;

        var available = Directory.EnumerateDirectories(libDir)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tfm in _tfmPreference)
        {
            if (!available.Contains(tfm)) continue;
            foreach (var dll in Directory.GetFiles(Path.Combine(libDir, tfm), "*.dll"))
                yield return dll;
            yield break;
        }
    }

    private static string ResolveNuGetRoot()
    {
        try
        {
            var settings = Settings.LoadDefaultSettings(root: null);
            return SettingsUtility.GetGlobalPackagesFolder(settings);
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");
        }
    }

    public void Dispose()
    {
        _diagnosticsCts?.Cancel();
        _diagnosticsCts?.Dispose();
        _workspace.Dispose();
    }

    // ── Private model types ────────────────────────────────────────────────────

    private sealed record SimpleCompletionItem(string DisplayText, CompletionItemKind Kind, string? Description) : ICompletionItem
    {
        public string FilterText => DisplayText;
        public string SortText => DisplayText;
    }

    private sealed class SimpleCompletionList(IReadOnlyList<ICompletionItem> items) : ICompletionList
    {
        public IReadOnlyList<ICompletionItem> Items => items;
        public bool IsExclusive => false;
    }

    private sealed class SimpleSignatureHelpResult(
        IReadOnlyList<ISignatureHelpItem> items, int selectedIndex, int argumentIndex) : ISignatureHelpResult
    {
        public IReadOnlyList<ISignatureHelpItem> Items => items;
        public int SelectedItemIndex => selectedIndex;
        public int ArgumentIndex => argumentIndex;
    }

    private sealed class SimpleSignatureHelpItem(IMethodSymbol method) : ISignatureHelpItem
    {
        public string DisplaySignature { get; } = BuildSignature(method);
        public string? Documentation => null;
        public IReadOnlyList<ISignatureHelpParameter> Parameters { get; } =
            method.Parameters.Select(p => (ISignatureHelpParameter)new SimpleSignatureHelpParameter(p)).ToList();

        private static string BuildSignature(IMethodSymbol m)
        {
            var ps = string.Join(", ", m.Parameters.Select(p =>
                $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}"));
            return $"{m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {m.Name}({ps})";
        }
    }

    private sealed class SimpleSignatureHelpParameter(IParameterSymbol parameter) : ISignatureHelpParameter
    {
        public string Name => parameter.Name;
        public string DisplayParts => $"{parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {parameter.Name}";
        public string? Documentation => null;
    }

    private sealed record SimpleQuickInfoResult(string Content) : IQuickInfoResult;

    private sealed record SimpleDiagnostic(
        int StartOffset, int EndOffset, string Message,
        ContractSeverity Severity) : IDiagnosticInfo;

    private sealed record SimpleSemanticToken(
        int StartOffset, int Length, SemanticTokenKind Kind) : ISemanticToken;

}
