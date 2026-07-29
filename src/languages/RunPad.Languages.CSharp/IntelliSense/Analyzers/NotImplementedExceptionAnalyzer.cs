using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок третий проход) — idea ported (not copied — own implementation, see
// EmptyCatchClauseAnalyzer's own doc comment for the same rationale) from Roslynator's
// ThrowingOfNewNotImplementedExceptionAnalyzer (RCS1079, Apache License 2.0,
// https://github.com/dotnet/roslynator). Notebook-scenario-relevant: pasted/generated code often
// leaves stub members (`throw new NotImplementedException();`) behind — Info severity (not Warning),
// since the author usually wrote this deliberately and knows it's there; this is a low-key reminder,
// not a warning about a mistake.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NotImplementedExceptionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0003";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Member throws NotImplementedException",
        messageFormat: "This throws NotImplementedException — implementation may still be incomplete",
        category: "Reliability",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A flag for stub/incomplete code, often left behind in pasted or generated snippets.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeThrow, SyntaxKind.ThrowStatement, SyntaxKind.ThrowExpression);
    }

    private static void AnalyzeThrow(SyntaxNodeAnalysisContext context)
    {
        var expression = context.Node switch
        {
            ThrowStatementSyntax s => s.Expression,
            ThrowExpressionSyntax e => e.Expression,
            _ => null,
        };
        if (expression is not ObjectCreationExpressionSyntax objectCreation) return;

        var type = context.SemanticModel.GetTypeInfo(objectCreation, context.CancellationToken).Type;
        if (type is not null && type.ToDisplayString() == "System.NotImplementedException")
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation()));
    }
}
