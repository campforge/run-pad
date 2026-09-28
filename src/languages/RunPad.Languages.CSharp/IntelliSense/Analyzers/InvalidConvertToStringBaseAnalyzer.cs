using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести девяносто первый проход, batch) — own pick, not a Roslynator port. Mirror image of
// InvalidConvertToIntegralBaseAnalyzer (RPAD0238, проход 242 + extended проход 290): that one covers
// `Convert.ToIntXX(string, int fromBase)` parsing FROM a based string; this one covers the opposite
// direction, `Convert.ToString(numericValue, int toBase)` formatting a number INTO a based string —
// `Convert.ToString(Byte/Int16/Int32/Int64, int toBase)` are documented to throw `ArgumentException`
// unless `toBase` is exactly 2, 8, 10, or 16 (confirmed empirically via scratch `dotnet run` script for
// all four overloads, not assumed from docs alone — there is no UInt16/UInt32/UInt64 overload with
// `toBase`, same asymmetry as the fromBase family). Found while brainstorming this batch's candidate,
// which also surfaced and fixed an unrelated false-positive bug in
// PreferInstanceToStringOverConvertToStringForNumericAnalyzer (RPAD0220) — see that file's history.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidConvertToStringBaseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0278";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Convert.ToString with an unsupported base always throws ArgumentException",
        messageFormat: "'Convert.ToString(x, {0})' always throws ArgumentException at runtime — toBase must be 2, 8, 10, or 16",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Convert.ToString(byte/short/int/long, int toBase) throws ArgumentException unless toBase is exactly 2, 8, 10, or 16.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<int> ValidBases = ImmutableHashSet.Create(2, 8, 10, 16);

    private static readonly ImmutableHashSet<SpecialType> TargetTypes = ImmutableHashSet.Create(
        SpecialType.System_Byte, SpecialType.System_Int16, SpecialType.System_Int32, SpecialType.System_Int64);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString" })
            return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var baseArg }]) return;
        if (context.SemanticModel.GetConstantValue(baseArg, context.CancellationToken).Value is not int baseValue)
            return;
        if (ValidBases.Contains(baseValue)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ToString" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Convert") return;
        if (method.Parameters is not [{ Type.SpecialType: var valueSpecialType }, { Type.SpecialType: SpecialType.System_Int32 }])
            return;
        if (!TargetTypes.Contains(valueSpecialType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), baseValue));
    }
}
