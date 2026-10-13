using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `Thread.CurrentThread.ManagedThreadId` fetches the whole current-thread object just to read one int
// off it — `Environment.CurrentManagedThreadId` (.NET Core, well within this project's .NET 6 SDK
// floor) returns the same value directly, without the intermediate Thread lookup.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferEnvironmentCurrentManagedThreadIdAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0162";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Environment.CurrentManagedThreadId over Thread.CurrentThread.ManagedThreadId",
        messageFormat: "'Thread.CurrentThread.ManagedThreadId' can be simplified to 'Environment.CurrentManagedThreadId' — same value, without fetching the current Thread object first",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Environment.CurrentManagedThreadId returns the same value as Thread.CurrentThread.ManagedThreadId directly, without the intermediate Thread lookup.");

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
        if (memberAccess.Name.Identifier.Text != "ManagedThreadId") return;
        if (memberAccess.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "CurrentThread" } currentThreadAccess)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(memberAccess, ct).Symbol is not IPropertySymbol { Name: "ManagedThreadId" } property)
            return;
        if (property.ContainingType?.ToDisplayString() != "System.Threading.Thread") return;

        if (context.SemanticModel.GetSymbolInfo(currentThreadAccess, ct).Symbol is not IPropertySymbol
            { Name: "CurrentThread", IsStatic: true } currentThreadProperty)
        {
            return;
        }
        if (currentThreadProperty.ContainingType?.ToDisplayString() != "System.Threading.Thread") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.GetLocation()));
    }
}
