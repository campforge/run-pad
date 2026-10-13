using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Sibling of
// TimeSpanAddOrSubtractOperatorOverflowAnalyzer (RPAD0315) and DateTimeOrDateOnlyAddMethodOverflowAnalyzer
// (RPAD0313) — this covers the OPERATOR form neither of those does: `DateTime + TimeSpan` and `DateTime
// - TimeSpan` throw ArgumentOutOfRangeException (a DIFFERENT exception type from TimeSpan's own
// OverflowException-throwing operators) when the result falls outside DateTime's representable range —
// confirmed empirically via scratch `dotnet run` script (e.g. `DateTime.MaxValue + TimeSpan.FromDays
// (1)`). RPAD0313 only registers on `InvocationExpression` (the `.AddDays()`-style methods), never
// `BinaryExpression`, so the operator form was genuinely uncovered. The `DateTime` operand is scoped to
// `DateTime.MaxValue`/`MinValue` (the realistic overflow-triggering values, same scope choice as
// RPAD0315's own `TimeSpan.MaxValue`/`MinValue`); the `TimeSpan` operand reuses the identical constant-
// extraction shape as RPAD0315's `TryGetConstantTimeSpan` helper, duplicated per this project's existing
// small-helper precedent.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DateTimeAddOrSubtractOperatorOverflowAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0344";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "DateTime addition/subtraction always overflows",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — the result is outside DateTime's representable range",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DateTime + TimeSpan and DateTime - TimeSpan throw ArgumentOutOfRangeException when the result overflows DateTime's representable range.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.AddExpression, SyntaxKind.SubtractExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        var ct = context.CancellationToken;

        if (context.SemanticModel.GetSymbolInfo(binary, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.BuiltinOperator or MethodKind.UserDefinedOperator } opMethod)
            return;
        if (opMethod.ContainingType?.ToDisplayString() != "System.DateTime") return;
        if (opMethod.ReturnType.ToDisplayString() != "System.DateTime") return;

        if (TryGetConstantDateTime(context.SemanticModel, binary.Left, ct) is not { } left) return;
        if (TryGetConstantTimeSpan(context.SemanticModel, binary.Right, ct) is not { } right) return;

        var isAdd = binary.IsKind(SyntaxKind.AddExpression);
        if (!AlwaysOverflows(left, right, isAdd)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), binary.ToString()));
    }

    private static bool AlwaysOverflows(DateTime left, TimeSpan right, bool isAdd)
    {
        try
        {
            _ = isAdd ? left + right : left - right;
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    private static DateTime? TryGetConstantDateTime(SemanticModel model, ExpressionSyntax expr, CancellationToken ct)
    {
        if (expr is MemberAccessExpressionSyntax { Name.Identifier.Text: "MaxValue" or "MinValue" } fieldAccess
            && model.GetSymbolInfo(fieldAccess, ct).Symbol is IFieldSymbol { ContainingType: { Name: "DateTime" } fieldType }
            && fieldType.ContainingNamespace?.ToDisplayString() == "System")
        {
            return fieldAccess.Name.Identifier.Text == "MaxValue" ? DateTime.MaxValue : DateTime.MinValue;
        }
        return null;
    }

    private static TimeSpan? TryGetConstantTimeSpan(SemanticModel model, ExpressionSyntax expr, CancellationToken ct)
    {
        if (expr is MemberAccessExpressionSyntax { Name.Identifier.Text: "Zero" or "MaxValue" or "MinValue" } fieldAccess
            && model.GetSymbolInfo(fieldAccess, ct).Symbol is IFieldSymbol { ContainingType: { Name: "TimeSpan" } fieldType }
            && fieldType.ContainingNamespace?.ToDisplayString() == "System")
        {
            return fieldAccess.Name.Identifier.Text switch
            {
                "Zero" => TimeSpan.Zero,
                "MaxValue" => TimeSpan.MaxValue,
                _ => TimeSpan.MinValue,
            };
        }

        if (expr is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: var factoryName }, ArgumentList.Arguments: [{ Expression: var factoryArg }] } factoryInvocation
            && model.GetSymbolInfo(factoryInvocation, ct).Symbol is IMethodSymbol { ContainingType: { Name: "TimeSpan" } factoryType }
            && factoryType.ContainingNamespace?.ToDisplayString() == "System"
            && model.GetConstantValue(factoryArg, ct) is { HasValue: true, Value: { } rawValue }
            && ToDouble(rawValue) is { } value)
        {
            return factoryName switch
            {
                "FromSeconds" => TimeSpan.FromSeconds(value),
                "FromMilliseconds" => TimeSpan.FromMilliseconds(value),
                "FromMinutes" => TimeSpan.FromMinutes(value),
                "FromHours" => TimeSpan.FromHours(value),
                "FromDays" => TimeSpan.FromDays(value),
                _ => (TimeSpan?)null,
            };
        }

        return null;
    }

    private static double? ToDouble(object value) => value switch
    {
        double d => d,
        int i => i,
        long l => l,
        _ => null,
    };
}
