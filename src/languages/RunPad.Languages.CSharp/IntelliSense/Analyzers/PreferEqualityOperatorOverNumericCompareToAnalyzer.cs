using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести двадцатый проход, batch) — own pick, not a Roslynator port. A sibling shape to
// PreferEqualityOperatorOverDateTimeCompareAnalyzer (RPAD0173, проход 187 — that one is the STATIC
// `DateTime.Compare(a, b) == 0` form). This is the INSTANCE `x.CompareTo(y) == 0` form: `x.CompareTo(y)`
// computes a full three-way ordering (negative/zero/positive) just to throw away everything except
// whether it was zero — `x == y` says the same thing directly. Restricted to the built-in numeric
// types (and char), where the `==`/`!=` operators are guaranteed to exist and to agree with CompareTo's
// zero result — a custom IComparable<T> type could define CompareTo and == with different semantics,
// so this deliberately doesn't try to generalize past the types where that can't happen.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferEqualityOperatorOverNumericCompareToAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0205";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer an equality operator over CompareTo(...) == 0",
        messageFormat: "'x.CompareTo(y) {0} 0' can be replaced with 'x {0} y' — CompareTo computes a full three-way ordering just to test whether it was zero",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "x.CompareTo(y) == 0 (or != 0) on a numeric type says the same thing the == / != operators already say directly, without computing an unused ordering.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<SpecialType> SafeTypes = ImmutableHashSet.Create(
        SpecialType.System_Byte, SpecialType.System_SByte,
        SpecialType.System_Int16, SpecialType.System_UInt16,
        SpecialType.System_Int32, SpecialType.System_UInt32,
        SpecialType.System_Int64, SpecialType.System_UInt64,
        SpecialType.System_Single, SpecialType.System_Double, SpecialType.System_Decimal,
        SpecialType.System_Char);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        if (binary.Left is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "CompareTo", Expression: var receiver },
                ArgumentList.Arguments.Count: 1,
            } compareToInvocation)
        {
            return;
        }

        var ct = context.CancellationToken;
        var rightConstant = context.SemanticModel.GetConstantValue(binary.Right, ct);
        if (rightConstant.Value is not 0) return;

        if (context.SemanticModel.GetSymbolInfo(compareToInvocation, ct).Symbol is not IMethodSymbol { Name: "CompareTo" } compareToMethod)
            return;

        var receiverType = context.SemanticModel.GetTypeInfo(receiver, ct).Type;
        if (receiverType?.SpecialType is not { } specialType || !SafeTypes.Contains(specialType))
            return;
        if (compareToMethod.ContainingType?.SpecialType != specialType) return;

        var operatorText = binary.IsKind(SyntaxKind.EqualsExpression) ? "==" : "!=";
        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), operatorText));
    }
}
