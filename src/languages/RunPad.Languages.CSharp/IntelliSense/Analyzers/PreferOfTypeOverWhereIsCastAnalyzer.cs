using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят пятый проход, batch) — own pick, not a Roslynator port.
// `xs.Where(x => x is T).Cast<T>()` filters down to the elements of type T and then casts them — two
// passes and two intermediate iterators for something `xs.OfType<T>()` already does directly, in one
// pass, without ever needing an explicit cast.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferOfTypeOverWhereIsCastAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0092";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer OfType over Where(is) followed by Cast",
        messageFormat: "Use 'OfType<T>()' instead of 'Where(x => x is T).Cast<T>()' — OfType does the filter and cast in a single pass",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Where(x => x is T).Cast<T>() filters and casts in two separate passes; OfType<T>() does both in one.");

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
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { Identifier.Text: "Cast", TypeArgumentList.Arguments: [var castTypeArg] },
                Expression: InvocationExpressionSyntax whereInvocation,
            })
        {
            return;
        }

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return;

        if (whereInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Where" }) return;
        if (whereInvocation.ArgumentList.Arguments is not [{ Expression: var lambdaExpression }]) return;
        if (!IsRealEnumerableMethod(context, whereInvocation, ct)) return;

        var (paramName, body) = lambdaExpression switch
        {
            SimpleLambdaExpressionSyntax { Body: ExpressionSyntax exprBody } simple =>
                (simple.Parameter.Identifier.Text, exprBody),
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters: [var soleParam], Body: ExpressionSyntax exprBody } paren =>
                (soleParam.Identifier.Text, exprBody),
            _ => (null, null),
        };
        if (paramName is null) return;

        // `x is T` with no pattern variable parses as a plain BinaryExpressionSyntax IsExpression.
        if (body is not BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.IsExpression,
                Left: IdentifierNameSyntax identifier,
                Right: TypeSyntax patternType,
            })
        {
            return;
        }
        if (identifier.Identifier.Text != paramName) return;

        var castType = context.SemanticModel.GetTypeInfo(castTypeArg, ct).Type;
        var predicateType = context.SemanticModel.GetTypeInfo(patternType, ct).Type;
        if (castType is null || predicateType is null || !SymbolEqualityComparer.Default.Equals(castType, predicateType))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
