using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "literal array length that always throws" theme as InvalidGuidByteArrayLengthAnalyzer (RPAD0266), for a different
// type: `new IPAddress(byte[])` requires the array to be EXACTLY 4 bytes
// (IPv4) or EXACTLY 16 bytes (IPv6), throwing ArgumentException for any other length (confirmed
// empirically before writing this). A separate analyzer rather than an extension of
// InvalidIPAddressLiteralAnalyzer (RPAD0249, `IPAddress.Parse` string-format validation) — same TYPE,
// but a different constructor/mechanism, matching this project's established precedent (e.g.
// NegativeVersionComponentAnalyzer/RPAD0265 kept separate from InvalidVersionLiteralFormatAnalyzer/
// RPAD0247 for the same reason). Scoped to a LITERAL array whose length is known at the call site, same
// as RPAD0266.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidIPAddressByteArrayLengthAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0267";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "IPAddress byte array constructor argument has the wrong length",
        messageFormat: "'new IPAddress(...)' always throws ArgumentException at runtime — the byte array must have exactly 4 (IPv4) or 16 (IPv6) elements, but this one has {0}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new IPAddress(byte[]) requires exactly 4 bytes for an IPv4 address or exactly 16 bytes for an IPv6 address.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var arrayArg }] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Net.IPAddress") return;
        if (ctor.Parameters is not [{ Type: IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte } }])
            return;

        var actualLength = TryGetArrayLength(arrayArg, context.SemanticModel, ct);
        if (actualLength is null or 4 or 16) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), actualLength));
    }

    private static int? TryGetArrayLength(ExpressionSyntax expression, SemanticModel semanticModel, System.Threading.CancellationToken ct)
    {
        if (expression is ArrayCreationExpressionSyntax arrayCreation)
        {
            if (arrayCreation.Initializer is { } init) return init.Expressions.Count;
            if (arrayCreation.Type.RankSpecifiers is [{ Sizes: [var sizeExpr] }, ..]
                && semanticModel.GetConstantValue(sizeExpr, ct).Value is int size)
            {
                return size;
            }
            return null;
        }
        if (expression is ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInit })
            return implicitInit.Expressions.Count;
        if (expression is CollectionExpressionSyntax collectionExpr)
            return collectionExpr.Elements.Count;
        return null;
    }
}
