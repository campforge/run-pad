using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят первый проход, batch) — own pick, not a Roslynator port.
// `dict.Keys.Count`/`dict.Values.Count` reads the count through the Keys/Values view collection — but
// a dictionary's Keys and Values collections always have exactly as many entries as the dictionary
// itself, so `dict.Count` gives the identical number directly, without going through the view.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantKeysCountAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0119";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use the dictionary's own Count instead of Keys.Count/Values.Count",
        messageFormat: "'{0}.Count' goes through the {0} view collection — use the dictionary's own 'Count' directly",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Keys.Count/Values.Count are always equal to the dictionary's own Count — reading it directly skips the view collection.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;
        if (memberAccess.Name.Identifier.Text != "Count") return;
        if (memberAccess.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Keys" or "Values" } viewAccess)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(memberAccess, ct).Symbol is not IPropertySymbol { Name: "Count" }) return;
        if (context.SemanticModel.GetSymbolInfo(viewAccess, ct).Symbol is not IPropertySymbol) return;

        var dictionaryType = context.SemanticModel.GetTypeInfo(viewAccess.Expression, ct).Type;
        if (dictionaryType is null || !HasCountProperty(dictionaryType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.GetLocation(), viewAccess.Name.Identifier.Text));
    }

    private static bool HasCountProperty(ITypeSymbol type)
    {
        foreach (var candidate in EnumerateSelfAndInterfaces(type))
        {
            if (candidate.GetMembers("Count").OfType<IPropertySymbol>().Any()) return true;
        }
        return false;
    }

    private static IEnumerable<ITypeSymbol> EnumerateSelfAndInterfaces(ITypeSymbol type)
    {
        yield return type;
        foreach (var iface in type.AllInterfaces) yield return iface;
    }
}
