using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `AppDomain.CurrentDomain.BaseDirectory` reaches through the current AppDomain just to read one
// string off it — `AppContext.BaseDirectory` (.NET Core, well within this project's .NET 6 SDK floor)
// returns the same path directly, without the AppDomain lookup (a concept .NET Core mostly kept only
// for compatibility).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferAppContextBaseDirectoryAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0163";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer AppContext.BaseDirectory over AppDomain.CurrentDomain.BaseDirectory",
        messageFormat: "'AppDomain.CurrentDomain.BaseDirectory' can be simplified to 'AppContext.BaseDirectory' — same value, without going through the current AppDomain",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "AppContext.BaseDirectory returns the same path as AppDomain.CurrentDomain.BaseDirectory directly, without the intermediate AppDomain lookup.");

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
        if (memberAccess.Name.Identifier.Text != "BaseDirectory") return;
        if (memberAccess.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "CurrentDomain" } currentDomainAccess)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(memberAccess, ct).Symbol is not IPropertySymbol { Name: "BaseDirectory" } property)
            return;
        if (property.ContainingType?.ToDisplayString() != "System.AppDomain") return;

        if (context.SemanticModel.GetSymbolInfo(currentDomainAccess, ct).Symbol is not IPropertySymbol
            { Name: "CurrentDomain", IsStatic: true } currentDomainProperty)
        {
            return;
        }
        if (currentDomainProperty.ContainingType?.ToDisplayString() != "System.AppDomain") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.GetLocation()));
    }
}
