using System.Text;
using DotPad.Contracts.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace DotPad.Languages.CSharp.IntelliSense;

public sealed class CSharpIntelliSenseService : IIntelliSenseService
{
    private static readonly string _preamble =
        "using System;\n" +
        "using System.Collections.Generic;\n" +
        "using System.IO;\n" +
        "using System.Linq;\n" +
        "using System.Text;\n" +
        "using System.Threading;\n" +
        "using System.Threading.Tasks;\n\n";

    private static readonly int _preambleLength = _preamble.Length;

    private static readonly SymbolDisplayFormat _briefFormat =
        new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private readonly IReadOnlyList<MetadataReference> _references;
    private readonly ILogger<CSharpIntelliSenseService> _logger;
    private readonly object _lock = new();
    private CSharpCompilation? _compilation;
    private string _lastCode = "";

    public CSharpIntelliSenseService(IEnumerable<string> referencePaths, ILogger<CSharpIntelliSenseService> logger)
    {
        _logger = logger;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>();

        foreach (var path in referencePaths)
        {
            if (File.Exists(path) && seen.Add(path))
                refs.Add(MetadataReference.CreateFromFile(path));
        }

        _references = refs;
    }

    // ── Compilation cache ─────────────────────────────────────────────────────

    private CSharpCompilation GetCompilation(string userCode)
    {
        var fullCode = _preamble + userCode;

        lock (_lock)
        {
            if (_compilation != null && _lastCode == userCode)
                return _compilation;

            if (_compilation != null)
            {
                // Incremental reparse — fast path
                var oldTree = _compilation.SyntaxTrees[0];
                var newTree = oldTree.WithChangedText(SourceText.From(fullCode, Encoding.UTF8));
                _compilation = _compilation.ReplaceSyntaxTree(oldTree, newTree);
            }
            else
            {
                var tree = CSharpSyntaxTree.ParseText(fullCode,
                    new CSharpParseOptions(LanguageVersion.Latest));
                _compilation = CSharpCompilation.Create(
                    "__dotpad_intellisense",
                    [tree],
                    _references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                        nullableContextOptions: NullableContextOptions.Enable));
            }

            _lastCode = userCode;
            return _compilation;
        }
    }

    // ── Completion ────────────────────────────────────────────────────────────

    public Task<ICompletionList?> GetCompletionsAsync(
        string code, int position, CompletionTrigger trigger = default, CancellationToken cancellationToken = default)
        => Task.Run(() => (ICompletionList?)TryGetCompletions(code, position, trigger), cancellationToken);

    private ICompletionList? TryGetCompletions(string code, int position, CompletionTrigger trigger)
    {
        try
        {
            var compilation = GetCompilation(code);
            var tree = compilation.SyntaxTrees[0];
            var root = tree.GetRoot();
            var model = compilation.GetSemanticModel(tree);
            var pos = position + _preambleLength;

            IEnumerable<ISymbol> symbols;

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

                // LookupSymbols handles inheritance, array base members, and extension methods
                symbols = model.LookupSymbols(pos, container: type, includeReducedExtensionMethods: true);
            }
            else
            {
                symbols = model.LookupSymbols(pos);
            }

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
        catch (Exception ex) { _logger.LogWarning(ex, "Completions failed"); return null; }
    }

    private static ICompletionItem? ToItem(ISymbol symbol)
    {
        return symbol switch
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
                => new SimpleCompletionItem(t.Name, t.TypeKind == TypeKind.Interface ? CompletionItemKind.Interface : CompletionItemKind.Class, t.ToDisplayString(_briefFormat)),
            _ => null
        };
    }

    // ── Signature Help ────────────────────────────────────────────────────────

    public Task<ISignatureHelpResult?> GetSignatureHelpAsync(
        string code, int position, char? triggerCharacter = null, CancellationToken cancellationToken = default)
        => Task.Run(() => (ISignatureHelpResult?)TryGetSignatureHelp(code, position), cancellationToken);

    private ISignatureHelpResult? TryGetSignatureHelp(string code, int position)
    {
        try
        {
            var compilation = GetCompilation(code);
            var tree = compilation.SyntaxTrees[0];
            var root = tree.GetRoot();
            var model = compilation.GetSemanticModel(tree);
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

    // ── Quick Info ────────────────────────────────────────────────────────────

    public Task<IQuickInfoResult?> GetQuickInfoAsync(
        string code, int position, CancellationToken cancellationToken = default)
        => Task.Run(() => (IQuickInfoResult?)TryGetQuickInfo(code, position), cancellationToken);

    private IQuickInfoResult? TryGetQuickInfo(string code, int position)
    {
        try
        {
            var compilation = GetCompilation(code);
            var tree = compilation.SyntaxTrees[0];
            var root = tree.GetRoot();
            var model = compilation.GetSemanticModel(tree);
            var pos = position + _preambleLength;

            var token = root.FindToken(pos);
            if (token == default) return null;

            var symInfo = model.GetSymbolInfo(token.Parent!);
            var symbol = symInfo.Symbol ?? symInfo.CandidateSymbols.FirstOrDefault()
                ?? (ISymbol?)model.GetTypeInfo(token.Parent!).Type;

            if (symbol == null) return null;

            var content = symbol.ToDisplayString(_briefFormat);
            return new SimpleQuickInfoResult(content);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Quick info failed"); return null; }
    }

    public void Dispose() { }

    // ── Private model types ───────────────────────────────────────────────────

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
}
