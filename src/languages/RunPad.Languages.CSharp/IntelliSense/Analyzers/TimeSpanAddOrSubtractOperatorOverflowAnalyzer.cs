using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "run the real
// validator" theme as TimeSpanConstructorOverflowAnalyzer (RPAD0314, this same batch): `TimeSpan1 +
// TimeSpan2` and `TimeSpan1 - TimeSpan2` throw OverflowException when the result overflows TimeSpan's
// representable range — confirmed empirically via scratch `dotnet run` script, not assumed (e.g.
// `TimeSpan.MaxValue + TimeSpan.FromDays(1)`). Reuses the same constant-`TimeSpan`-value extraction
// shape as InvalidRegexMatchTimeoutAnalyzer's `TryGetConstantTimeSpan` helper (RPAD0279) —
// `TimeSpan.Zero`/`TimeSpan.FromXxx(literalNumber)` — extended here to also recognize the
// `TimeSpan.MaxValue`/`TimeSpan.MinValue` fields (needed for the most realistic overflow-triggering
// expressions), duplicated rather than shared per this project's existing small-helper precedent. Both
// operands must resolve to a known constant `TimeSpan` value for this analyzer to proceed; an operand
// built from a plain `new TimeSpan(...)` literal constructor call is a possible future extension, left
// out here to keep the operand-extraction helper's scope manageable.
// Extended to also cover `TimeSpan * double`/`double *
// TimeSpan`, `TimeSpan / double`, and unary `-TimeSpan` — confirmed empirically to throw the same
// OverflowException for the same "result outside TimeSpan's representable range" reason (e.g.
// `TimeSpan.MaxValue * 2`, `-TimeSpan.MinValue`). `TimeSpan / TimeSpan` (returns `double`, not
// `TimeSpan`) is naturally excluded by the existing return-type check. The scalar operand for
// multiply/divide must itself be a literal constant (`int`/`long`/`double`).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TimeSpanAddOrSubtractOperatorOverflowAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0315";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "TimeSpan arithmetic always overflows",
        messageFormat: "'{0}' always throws OverflowException at runtime — the result is outside TimeSpan's representable range",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "TimeSpan1 + TimeSpan2, TimeSpan1 - TimeSpan2, TimeSpan * double, TimeSpan / double, and -TimeSpan throw OverflowException when the result overflows TimeSpan's representable range.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.AddExpression, SyntaxKind.SubtractExpression, SyntaxKind.MultiplyExpression, SyntaxKind.DivideExpression);
        context.RegisterSyntaxNodeAction(AnalyzeUnaryMinus, SyntaxKind.UnaryMinusExpression);
    }

    private static void AnalyzeUnaryMinus(SyntaxNodeAnalysisContext context)
    {
        var unary = (PrefixUnaryExpressionSyntax)context.Node;
        var ct = context.CancellationToken;

        if (context.SemanticModel.GetSymbolInfo(unary, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.BuiltinOperator or MethodKind.UserDefinedOperator } opMethod)
            return;
        if (opMethod.ContainingType?.ToDisplayString() != "System.TimeSpan") return;
        if (opMethod.ReturnType.ToDisplayString() != "System.TimeSpan") return;

        if (TryGetConstantTimeSpan(context.SemanticModel, unary.Operand, ct) is not { } operand) return;

        bool alwaysOverflows;
        try
        {
            _ = -operand;
            alwaysOverflows = false;
        }
        catch (OverflowException)
        {
            alwaysOverflows = true;
        }
        if (!alwaysOverflows) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, unary.GetLocation(), unary.ToString()));
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        var ct = context.CancellationToken;

        if (context.SemanticModel.GetSymbolInfo(binary, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.BuiltinOperator or MethodKind.UserDefinedOperator } opMethod)
            return;
        if (opMethod.ContainingType?.ToDisplayString() != "System.TimeSpan") return;
        if (opMethod.ReturnType.ToDisplayString() != "System.TimeSpan") return;

        if (binary.IsKind(SyntaxKind.MultiplyExpression) || binary.IsKind(SyntaxKind.DivideExpression))
        {
            AnalyzeMultiplyOrDivide(context, binary);
            return;
        }

        if (TryGetConstantTimeSpan(context.SemanticModel, binary.Left, ct) is not { } left) return;
        if (TryGetConstantTimeSpan(context.SemanticModel, binary.Right, ct) is not { } right) return;

        var isAdd = binary.IsKind(SyntaxKind.AddExpression);
        if (!AlwaysOverflows(left, right, isAdd)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), binary.ToString()));
    }

    private static void AnalyzeMultiplyOrDivide(SyntaxNodeAnalysisContext context, BinaryExpressionSyntax binary)
    {
        var ct = context.CancellationToken;
        var isMultiply = binary.IsKind(SyntaxKind.MultiplyExpression);

        TimeSpan? timeSpanOperand;
        double? scalarOperand;
        if (TryGetConstantTimeSpan(context.SemanticModel, binary.Left, ct) is { } leftSpan)
        {
            timeSpanOperand = leftSpan;
            scalarOperand = TryGetConstantScalar(context.SemanticModel, binary.Right, ct);
        }
        else if (isMultiply && TryGetConstantTimeSpan(context.SemanticModel, binary.Right, ct) is { } rightSpan)
        {
            timeSpanOperand = rightSpan;
            scalarOperand = TryGetConstantScalar(context.SemanticModel, binary.Left, ct);
        }
        else
        {
            return;
        }
        if (timeSpanOperand is not { } span || scalarOperand is not { } scalar) return;

        bool alwaysOverflows;
        try
        {
            _ = isMultiply ? span * scalar : span / scalar;
            alwaysOverflows = false;
        }
        catch (OverflowException)
        {
            alwaysOverflows = true;
        }
        if (!alwaysOverflows) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), binary.ToString()));
    }

    private static double? TryGetConstantScalar(SemanticModel model, ExpressionSyntax expr, CancellationToken ct) =>
        model.GetConstantValue(expr, ct) is { HasValue: true, Value: { } value } ? ToDouble(value) : null;

    private static bool AlwaysOverflows(TimeSpan left, TimeSpan right, bool isAdd)
    {
        try
        {
            _ = isAdd ? left + right : left - right;
            return false;
        }
        catch (OverflowException)
        {
            return true;
        }
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
