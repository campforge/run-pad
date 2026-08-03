using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести второй проход, batch) — own pick, not a Roslynator port.
// `x.ToString().PadLeft(n, '0')` allocates an intermediate string via ToString() just to zero-pad it
// afterwards — `x.ToString("Dn")` produces the zero-padded digits directly, in one call, with no
// intermediate allocation. Restricted to the unsigned integer types (byte/ushort/uint/ulong): for
// signed types the two are NOT equivalent for negative values — PadLeft('0') inserts zeros to the left
// of the minus sign (e.g. "00-5"), while the D format specifier pads the digits and keeps the sign in
// front (e.g. "-0005") — confirmed empirically, not assumed, before writing this analyzer.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferDFormatSpecifierOverToStringPadLeftAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0190";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer the D format specifier over ToString().PadLeft('0')",
        messageFormat: "'ToString().PadLeft(n, '0')' on an unsigned integer allocates a string just to pad it — 'ToString(\"Dn\")' produces the same zero-padded digits directly",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "For unsigned integers, ToString().PadLeft(n, '0') and ToString(\"Dn\") produce identical results, but the D format specifier avoids the intermediate ToString() allocation and the PadLeft call.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<SpecialType> UnsignedIntegerTypes = ImmutableHashSet.Create(
        SpecialType.System_Byte,
        SpecialType.System_UInt16,
        SpecialType.System_UInt32,
        SpecialType.System_UInt64);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "PadLeft",
                Expression: InvocationExpressionSyntax toStringInvocation,
            })
        {
            return;
        }
        if (invocation.ArgumentList.Arguments is not [_, var paddingCharArg])
            return;
        if (toStringInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString", Expression: var receiver })
            return;
        if (toStringInvocation.ArgumentList.Arguments.Count != 0) return;

        var ct = context.CancellationToken;
        var paddingCharConstant = context.SemanticModel.GetConstantValue(paddingCharArg.Expression, ct);
        if (paddingCharConstant.Value is not '0') return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "PadLeft" } padLeftMethod)
            return;
        if (padLeftMethod.ContainingType?.SpecialType != SpecialType.System_String) return;

        if (context.SemanticModel.GetSymbolInfo(toStringInvocation, ct).Symbol is not IMethodSymbol { Name: "ToString" })
            return;

        var receiverType = context.SemanticModel.GetTypeInfo(receiver, ct).Type;
        if (receiverType?.SpecialType is not { } specialType || !UnsignedIntegerTypes.Contains(specialType))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
