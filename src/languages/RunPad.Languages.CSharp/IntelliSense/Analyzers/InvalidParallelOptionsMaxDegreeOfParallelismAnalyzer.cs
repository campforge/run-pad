using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Threading.Tasks.ParallelOptions.
// MaxDegreeOfParallelism` — throws `ArgumentOutOfRangeException` for `0` ("must be a non-zero value") or
// any value ≤ -2; `-1` (the "unlimited" sentinel) and any positive value are valid. Confirmed invariant
// across en-US/de-DE/ar-SA/ja-JP. No side effects — `ParallelOptions` is a plain POCO, property
// assignment never touches the ThreadPool or starts any parallel work.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidParallelOptionsMaxDegreeOfParallelismAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0455";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ParallelOptions.MaxDegreeOfParallelism assignment always throws",
        messageFormat: "'ParallelOptions.MaxDegreeOfParallelism = {0}' always throws ArgumentOutOfRangeException at runtime — must be -1 (unlimited) or a positive value",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ParallelOptions.MaxDegreeOfParallelism throws ArgumentOutOfRangeException for 0 or any value <= -2.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "MaxDegreeOfParallelism" } access)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(assignment.Right, ct).Value is not int value) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: "MaxDegreeOfParallelism" })
            return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Threading.Tasks.ParallelOptions") return;

        try
        {
            new ParallelOptions().MaxDegreeOfParallelism = value;
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), value));
        }
    }
}
