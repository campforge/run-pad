using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести тридцатый проход, batch) — own pick, not a Roslynator port.
// `Convert.ToString(numericValue)` / `Convert.ToString(numericValue, provider)` are documented to do
// nothing more than call `numericValue.ToString()` / `numericValue.ToString(provider)` internally — the
// Convert wrapper adds an extra static call for no behavioral difference. Deliberately scoped to
// numeric SpecialTypes only (byte/sbyte/short/ushort/int/uint/long/ulong/float/double/decimal) — unlike
// RedundantIdentityConvertCallAnalyzer's (RPAD0065) doc comment explains for the Convert.ToXxx(Xxx)
// identity-conversion family, Convert.ToString(string) is NOT included here either (it turns null into
// "", not a true passthrough), and this analyzer's own scope excludes it the same way by only matching
// numeric receiver types, which can never be null.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferInstanceToStringOverConvertToStringForNumericAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0220";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer the instance ToString() over Convert.ToString for a numeric value",
        messageFormat: "'Convert.ToString(x{0})' can be replaced with 'x.ToString({1})' — Convert.ToString delegates to it internally",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Convert.ToString(numericValue[, provider]) calls numericValue.ToString([provider]) internally — calling it directly skips the extra static wrapper.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<SpecialType> NumericTypes = ImmutableHashSet.Create(
        SpecialType.System_Byte, SpecialType.System_SByte,
        SpecialType.System_Int16, SpecialType.System_UInt16,
        SpecialType.System_Int32, SpecialType.System_UInt32,
        SpecialType.System_Int64, SpecialType.System_UInt64,
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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString" })
            return;
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count is not 1 and not 2) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ToString" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Convert") return;

        var hasProvider = arguments.Count == 2;
        var value = arguments[0].Expression;

        var valueType = context.SemanticModel.GetTypeInfo(value, ct).Type;
        if (valueType?.SpecialType is not { } specialType || !NumericTypes.Contains(specialType)) return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            invocation.GetLocation(),
            hasProvider ? ", provider" : string.Empty,
            hasProvider ? "provider" : string.Empty));
    }
}
