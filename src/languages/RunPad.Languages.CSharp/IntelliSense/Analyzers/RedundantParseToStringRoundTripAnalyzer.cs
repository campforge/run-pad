using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "redundant round trip" theme as RedundantIdentityConvertCallAnalyzer (RPAD0065, проход 160), but for
// `T.Parse(x.ToString())` instead of `Convert.ToT(x)`: when x's static type is already T, converting
// it to a string just to immediately parse it back out reconstructs the same value through a wasteful
// (and, for floating-point types, potentially lossy — ToString()'s default precision can drop digits)
// string round trip.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantParseToStringRoundTripAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0097";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant Parse(ToString()) round trip",
        messageFormat: "'{0}.Parse(x.ToString())' round-trips through a string when x is already {0} — use x directly",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Parsing the ToString() of a value that's already the target numeric type reconstructs the same value through a wasteful (and potentially precision-losing) string round trip.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<SpecialType> ParseableNumericTypes = ImmutableHashSet.Create(
        SpecialType.System_Byte, SpecialType.System_SByte, SpecialType.System_Int16, SpecialType.System_UInt16,
        SpecialType.System_Int32, SpecialType.System_UInt32, SpecialType.System_Int64, SpecialType.System_UInt64,
        SpecialType.System_Single, SpecialType.System_Double, SpecialType.System_Decimal);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" }) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: InvocationExpressionSyntax toStringCall }]) return;
        if (toStringCall.ArgumentList.Arguments.Count != 0) return;
        if (toStringCall.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString", Expression: var receiverExpression })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Parse", Parameters.Length: 1 } parseMethod)
            return;
        if (parseMethod.ContainingType is not { } targetType || !ParseableNumericTypes.Contains(targetType.SpecialType))
            return;

        if (context.SemanticModel.GetSymbolInfo(toStringCall, ct).Symbol is not IMethodSymbol { Parameters.Length: 0, Name: "ToString" })
            return;

        var receiverType = context.SemanticModel.GetTypeInfo(receiverExpression, ct).Type;
        if (receiverType is null || receiverType.SpecialType != targetType.SpecialType) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), targetType.Name));
    }
}
