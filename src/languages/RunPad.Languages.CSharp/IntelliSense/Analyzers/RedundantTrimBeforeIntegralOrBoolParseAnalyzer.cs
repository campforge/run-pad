using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `int.Parse(s.Trim())` (and the other integral types, plus bool.Parse) trims s before parsing it —
// but the default NumberStyles for every integral Parse overload (NumberStyles.Integer) already
// includes AllowLeadingWhite/AllowTrailingWhite, and bool.Parse is separately documented to ignore
// surrounding whitespace on its own — so the Trim() call never changes the result. Deliberately
// excludes double/float/decimal — CultureSensitiveNumericParseAnalyzer (RPAD0098) already flags every
// call to THOSE types' Parse(string) for a different reason (missing culture), and this analyzer isn't
// trying to stack a second, unrelated diagnostic on the same call. Deliberately restricted to the
// (string) and (string, IFormatProvider) overloads (verified by parameter shape, not just method name)
// — an explicit NumberStyles argument could turn off the whitespace-allowing flags, so that overload is
// left alone rather than assumed safe.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantTrimBeforeIntegralOrBoolParseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0221";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Remove a redundant Trim() before Parse",
        messageFormat: "'{0}.Parse(s.Trim())' can be replaced with '{0}.Parse(s)' — Parse already tolerates surrounding whitespace",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "The integral types' default NumberStyles (and bool.Parse's own documented behavior) already allow leading/trailing whitespace, so trimming beforehand doesn't change the result.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<SpecialType> TargetTypes = ImmutableHashSet.Create(
        SpecialType.System_Byte, SpecialType.System_SByte,
        SpecialType.System_Int16, SpecialType.System_UInt16,
        SpecialType.System_Int32, SpecialType.System_UInt32,
        SpecialType.System_Int64, SpecialType.System_UInt64,
        SpecialType.System_Boolean);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: InvocationExpressionSyntax trimInvocation }, ..])
            return;
        if (trimInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Trim" })
            return;
        if (trimInvocation.ArgumentList.Arguments.Count != 0) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Parse" } method)
            return;
        if (method.ContainingType?.SpecialType is not { } specialType || !TargetTypes.Contains(specialType)) return;
        if (method.Parameters.Length is not (1 or 2)) return;
        if (method.Parameters.Length == 2 &&
            (method.Parameters[1].Type is not { Name: "IFormatProvider" } secondParamType ||
             secondParamType.ContainingNamespace?.ToDisplayString() != "System"))
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(trimInvocation, ct).Symbol is not IMethodSymbol { Name: "Trim" } trimMethod)
            return;
        if (trimMethod.ContainingType?.SpecialType != SpecialType.System_String) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), method.ContainingType.Name));
    }
}
