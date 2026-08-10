using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста сорок восьмой проход, batch) — own pick, not a Roslynator port. Same "literal
// receiver, known contents" theme as EnumerableSumOverflowAnalyzer (this same batch): `.ToDictionary
// (keySelector)` on a freshly created array literal that contains two elements which would map to the
// SAME key always throws ArgumentException ("An item with the same key has already been added") —
// confirmed empirically via scratch `dotnet run` script, not assumed. Deliberately scoped to only the
// IDENTITY key selector (`x => x`) — proving what an arbitrary key selector lambda produces for two
// literal elements would need to actually evaluate arbitrary expression bodies, which this analyzer
// doesn't attempt; the identity case needs no evaluation at all, since the key IS the element. Only
// proceeds when every element is itself a literal (constant) value, comparing via `Equals` on the
// extracted constants.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EnumerableToDictionaryDuplicateKeyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0317";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Enumerable.ToDictionary(x => x) on a literal array with a duplicate element always throws",
        messageFormat: "'xs.ToDictionary(x => x)' always throws ArgumentException at runtime — the array literal has a duplicate element, which would be used as a duplicate key",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Enumerable.ToDictionary(keySelector) throws ArgumentException when two elements map to the same key, provable here for the identity key selector because the receiver is a freshly created array literal with known, literal elements.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToDictionary" } access)
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var selectorArg }]) return;
        if (!IsIdentityLambda(selectorArg)) return;

        var elements = TryGetArrayLiteralElements(access.Expression);
        if (elements is not { Length: > 1 } elementList) return;

        var ct = context.CancellationToken;
        var values = new object[elementList.Length];
        for (var i = 0; i < elementList.Length; i++)
        {
            var constant = context.SemanticModel.GetConstantValue(elementList[i], ct);
            if (!constant.HasValue || constant.Value is null) return;
            values[i] = constant.Value;
        }
        if (!HasDuplicate(values)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ToDictionary" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsIdentityLambda(ExpressionSyntax expression) =>
        expression is SimpleLambdaExpressionSyntax { Body: IdentifierNameSyntax bodyName } lambda
        && bodyName.Identifier.Text == lambda.Parameter.Identifier.Text;

    private static bool HasDuplicate(object[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            for (var j = i + 1; j < values.Length; j++)
            {
                if (Equals(values[i], values[j])) return true;
            }
        }
        return false;
    }

    private static ImmutableArray<ExpressionSyntax>? TryGetArrayLiteralElements(ExpressionSyntax expression)
    {
        if (expression is ArrayCreationExpressionSyntax { Initializer: { } explicitInit })
            return [.. explicitInit.Expressions];
        if (expression is ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInit })
            return [.. implicitInit.Expressions];
        if (expression is CollectionExpressionSyntax collectionExpr)
            return [.. collectionExpr.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression)];
        return null;
    }
}
