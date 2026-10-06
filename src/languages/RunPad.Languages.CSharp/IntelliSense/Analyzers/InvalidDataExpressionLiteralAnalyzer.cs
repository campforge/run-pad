using System.Collections.Immutable;
using System.Data;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (442-й проход, batch) — candidate #1 from the seventh research subagent survey (441→442-й
// проход, full tool access, empirically self-verified), re-verified independently before implementing.
// `DataColumn.Expression` (property assignment), `DataTable.Select(string, ...)` and
// `DataTable.Compute(string, string)` (first argument of each) all feed the SAME internal expression
// parser — a malformed literal throws `System.Data.SyntaxErrorException` immediately, with no dependency
// on the table's actual schema (confirmed empirically against both a 0-column and a multi-column table)
// and no dependency on the current culture (confirmed across en-US/de-DE/ar-SA/ja-JP). Deliberately scoped
// to catch ONLY `SyntaxErrorException`, never `EvaluateException` or `FormatException`: a syntactically
// well-formed expression referencing a column that doesn't exist in THIS throwaway probe table (e.g.
// "Y + 1") throws `EvaluateException`, which is schema-dependent and says nothing about the literal's own
// well-formedness; the `#...#` date-literal syntax (e.g. "#notadate#") throws a culture-sensitive
// `FormatException` from an internal `DateTime` parse, which is excluded for the same reason the
// culture-dependence trap rules out any culture-sensitive throw elsewhere in this catalog. Also confirmed
// empirically lenient in ways worth noting: an empty string, and an unbalanced-parens string like "((",
// are both accepted without throwing — this analyzer never flags either. Follows the property-assignment
// shape from InvalidDbConnectionStringLiteralAnalyzer (RPAD0383) for `DataColumn.Expression`, and the
// instance-invocation shape from InvalidXmlDocumentLoadXmlLiteralAnalyzer (RPAD0415) for
// `DataTable.Select`/`DataTable.Compute`, combined into one analyzer since all three call shapes share the
// identical validation semantics and diagnostic message shape.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidDataExpressionLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0429";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "DataColumn/DataTable expression literal is not well-formed",
        messageFormat: "'{0}' always throws SyntaxErrorException at runtime — '{1}' is not a well-formed DataColumn expression",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DataColumn.Expression, DataTable.Select(filterExpression, ...) and DataTable.Compute(expression, filter) all share the same expression-syntax parser, which throws System.Data.SyntaxErrorException for a syntactically malformed expression such as an unterminated string, unbalanced bracket-name, or a missing operand.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Expression" } access)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(assignment.Right, ct).Value is not string expression) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: "Expression" } property)
            return;
        if (property.Type.SpecialType != SpecialType.System_String) return;

        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Data.DataColumn") return;

        if (!ThrowsSyntaxError(() =>
            {
                using var table = new DataTable();
                var column = table.Columns.Add("E");
                column.Expression = expression;
            }))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), "DataColumn.Expression = ...", expression));
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Select" or "Compute" } access)
            return;
        if (invocation.ArgumentList.Arguments.Count == 0) return;

        var ct = context.CancellationToken;
        var firstArg = invocation.ArgumentList.Arguments[0].Expression;
        if (context.SemanticModel.GetConstantValue(firstArg, ct).Value is not string expression) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                IsStatic: false,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }, ..],
                ContainingType: { } containingType,
            } method)
        {
            return;
        }
        if ((method.Name != "Select" && method.Name != "Compute") || containingType.ToDisplayString() != "System.Data.DataTable")
            return;

        var threw = method.Name == "Select"
            ? ThrowsSyntaxError(() => { using var table = new DataTable(); table.Select(expression); })
            : ThrowsSyntaxError(() => { using var table = new DataTable(); table.Compute(expression, string.Empty); });
        if (!threw) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), $"DataTable.{method.Name}(...)", expression));
    }

    private static bool ThrowsSyntaxError(Action probe)
    {
        try
        {
            probe();
            return false;
        }
        catch (SyntaxErrorException)
        {
            return true;
        }
        catch (EvaluateException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
