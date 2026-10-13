using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.Where(x => x is T).Cast<T>()` filters down to the elements of type T and then casts them — two
// passes and two intermediate iterators for something `xs.OfType<T>()` already does directly, in one
// pass, without ever needing an explicit cast.
// Extended to also cover the "as + null-check" shape —
// `xs.Select(x => x as T).Where(x => x != null)` — the same OfType<T>-replaceable idea, just written
// the other way around (project first with `as`, then filter out the nulls that produced).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferOfTypeOverWhereIsCastAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0092";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer OfType over Where(is)/Cast or Select(as)/Where(not null)",
        messageFormat: "Use 'OfType<T>()' instead — OfType does the filter and cast in a single pass",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Where(x => x is T).Cast<T>() and Select(x => x as T).Where(x => x != null) both filter and cast in two separate passes; OfType<T>() does both in one.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (IsWhereIsCast(context, invocation) || IsSelectAsWhereNotNull(context, invocation))
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsWhereIsCast(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count != 0) return false;
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { Identifier.Text: "Cast", TypeArgumentList.Arguments: [var castTypeArg] },
                Expression: InvocationExpressionSyntax whereInvocation,
            })
        {
            return false;
        }

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return false;

        if (whereInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Where" }) return false;
        if (whereInvocation.ArgumentList.Arguments is not [{ Expression: var lambdaExpression }]) return false;
        if (!IsRealEnumerableMethod(context, whereInvocation, ct)) return false;

        var (paramName, body) = ExtractLambda(lambdaExpression);
        if (paramName is null) return false;

        if (body is not BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.IsExpression,
                Left: IdentifierNameSyntax identifier,
                Right: TypeSyntax patternType,
            })
        {
            return false;
        }
        if (identifier.Identifier.Text != paramName) return false;

        var castType = context.SemanticModel.GetTypeInfo(castTypeArg, ct).Type;
        var predicateType = context.SemanticModel.GetTypeInfo(patternType, ct).Type;
        return castType is not null && predicateType is not null && SymbolEqualityComparer.Default.Equals(castType, predicateType);
    }

    private static bool IsSelectAsWhereNotNull(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Where", Expression: InvocationExpressionSyntax selectInvocation })
            return false;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var whereLambdaExpression }]) return false;

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return false;

        var (whereParamName, whereBody) = ExtractLambda(whereLambdaExpression);
        if (whereParamName is null || !IsNotNullCheck(whereBody, whereParamName)) return false;

        if (selectInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Select" }) return false;
        if (selectInvocation.ArgumentList.Arguments is not [{ Expression: var selectLambdaExpression }]) return false;
        if (!IsRealEnumerableMethod(context, selectInvocation, ct)) return false;

        var (selectParamName, selectBody) = ExtractLambda(selectLambdaExpression);
        if (selectParamName is null) return false;
        if (selectBody is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AsExpression, Left: IdentifierNameSyntax asIdentifier })
            return false;

        return asIdentifier.Identifier.Text == selectParamName;
    }

    private static (string? ParamName, ExpressionSyntax? Body) ExtractLambda(ExpressionSyntax lambdaExpression) => lambdaExpression switch
    {
        SimpleLambdaExpressionSyntax { Body: ExpressionSyntax exprBody } simple => (simple.Parameter.Identifier.Text, exprBody),
        ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters: [var soleParam], Body: ExpressionSyntax exprBody } paren =>
            (soleParam.Identifier.Text, exprBody),
        _ => (null, null),
    };

    private static bool IsNotNullCheck(ExpressionSyntax? body, string paramName) => body switch
    {
        BinaryExpressionSyntax
        {
            RawKind: (int)SyntaxKind.NotEqualsExpression,
            Left: IdentifierNameSyntax id,
            Right: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression },
        } when id.Identifier.Text == paramName => true,
        BinaryExpressionSyntax
        {
            RawKind: (int)SyntaxKind.NotEqualsExpression,
            Right: IdentifierNameSyntax id,
            Left: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression },
        } when id.Identifier.Text == paramName => true,
        IsPatternExpressionSyntax
        {
            Expression: IdentifierNameSyntax id,
            Pattern: UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression } },
        } when id.Identifier.Text == paramName => true, // x is not null
        _ => false,
    };

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
