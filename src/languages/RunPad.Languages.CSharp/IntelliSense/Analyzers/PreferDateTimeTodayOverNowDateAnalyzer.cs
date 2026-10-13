using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `DateTime.Now.Date` reads the current moment just to immediately strip its time-of-day component —
// `DateTime.Today` already returns exactly that value directly (it's implemented as DateTime.Now.Date
// internally), in one step instead of two.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferDateTimeTodayOverNowDateAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0172";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer DateTime.Today over DateTime.Now.Date",
        messageFormat: "'DateTime.Now.Date' can be simplified to 'DateTime.Today' — same value, in one step",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "DateTime.Today already returns the same value as DateTime.Now.Date directly, without the intermediate DateTime.Now read.");

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
        if (memberAccess.Name.Identifier.Text != "Date") return;
        if (memberAccess.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Now" } nowAccess)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(memberAccess, ct).Symbol is not IPropertySymbol { Name: "Date" } dateProperty)
            return;
        if (dateProperty.ContainingType?.ToDisplayString() != "System.DateTime") return;

        if (context.SemanticModel.GetSymbolInfo(nowAccess, ct).Symbol is not IPropertySymbol { Name: "Now", IsStatic: true } nowProperty)
            return;
        if (nowProperty.ContainingType?.ToDisplayString() != "System.DateTime") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.GetLocation()));
    }
}
