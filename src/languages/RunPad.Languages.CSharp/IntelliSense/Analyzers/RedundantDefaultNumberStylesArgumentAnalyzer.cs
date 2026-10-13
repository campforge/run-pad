using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `int.Parse(s, NumberStyles.Integer)` spells out exactly the style the parameterless `int.Parse(s)`
// overload already uses by default — NumberStyles.Integer is documented as the default style for every
// signed/unsigned integer type's Parse(string) overload. Scoped to the integer types specifically
// (byte/sbyte/short/ushort/int/uint/long/ulong): double/float/decimal default to compound styles
// (NumberStyles.Float | NumberStyles.AllowThousands, NumberStyles.Number) that are riskier to match
// reliably by simple syntax comparison, so those are deliberately left uncovered.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantDefaultNumberStylesArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0197";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant explicit default NumberStyles.Integer argument",
        messageFormat: "Passing 'NumberStyles.Integer' explicitly is redundant — the parameterless 'Parse(s)' overload already uses it by default",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "NumberStyles.Integer is already the default style every integer type's Parse(string) overload uses — passing it explicitly changes nothing.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<SpecialType> IntegerTypes = ImmutableHashSet.Create(
        SpecialType.System_Byte, SpecialType.System_SByte,
        SpecialType.System_Int16, SpecialType.System_UInt16,
        SpecialType.System_Int32, SpecialType.System_UInt32,
        SpecialType.System_Int64, SpecialType.System_UInt64);

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
        if (invocation.ArgumentList.Arguments is not [_, { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Integer" } stylesArg }])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Parse" } parseMethod)
            return;
        if (parseMethod.ContainingType?.SpecialType is not { } specialType || !IntegerTypes.Contains(specialType))
            return;

        if (context.SemanticModel.GetSymbolInfo(stylesArg, ct).Symbol is not IFieldSymbol { Name: "Integer" } stylesField)
            return;
        if (stylesField.ContainingType?.ToDisplayString() != "System.Globalization.NumberStyles") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
