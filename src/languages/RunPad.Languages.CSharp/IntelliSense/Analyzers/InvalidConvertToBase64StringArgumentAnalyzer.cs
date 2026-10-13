using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "literal array with a bad offset/length combination always throws" theme as
// InvalidEncodingGetStringArgumentAnalyzer/InvalidArrayAsSpanOrAsMemoryArgumentAnalyzer (RPAD0270/
// RPAD0271): `Convert.ToBase64String(byte[] inArray, int offset, int length)` throws
// ArgumentOutOfRangeException when offset or length is negative, or when `offset + length` exceeds the
// array's length (confirmed empirically before writing this — same boundary formula as the rest of this
// family). A separate analyzer rather than an extension of InvalidBase64LiteralLengthAnalyzer
// (RPAD0245, `Convert.FromBase64String` STRING validity, the opposite direction — decode, not encode)
// — same containing type, but a completely different mechanism (array-bounds check vs. Base64-alphabet/
// padding validity), matching this project's precedent of separating same-type-different-mechanism
// analyzers. Scoped to a LITERAL array whose length is known at the call site, same helper as
// RPAD0266-0271.
// Extended to also cover `Convert.ToHexString(byte[], int offset, int
// length)` — confirmed empirically to follow the EXACT same boundary formula as `ToBase64String`
// (offset/length negative, or offset + length > array length), so it reuses the same branch rather than
// duplicating it. Class name/`DiagnosticId` kept as-is, same precedent as other same-mechanism
// extensions in this catalog (e.g. `IntegralParseOverflowAnalyzer`/RPAD0258 keeping its name after being
// extended to `decimal`).
// Extended again to also cover `Convert.ToHexStringLower(byte[], int
// offset, int length)` — confirmed empirically to follow the identical boundary formula as `ToHexString`/
// `ToBase64String` (it differs only in producing lowercase hex digits, not in argument validation).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidConvertToBase64StringArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0272";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Convert.ToBase64String/ToHexString(byte[], offset, length) arguments are out of range",
        messageFormat: "'Convert.{0}(...)' always throws ArgumentOutOfRangeException at runtime — offset ({1}) and length ({2}) don't fit within a {3}-element array",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Convert.ToBase64String(byte[], int offset, int length) and Convert.ToHexString(byte[], int offset, int length) throw ArgumentOutOfRangeException when offset or length is negative, or offset + length exceeds the array's length.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToBase64String" or "ToHexString" or "ToHexStringLower" })
            return;
        var methodName = ((MemberAccessExpressionSyntax)invocation.Expression).Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arrayArg }, { Expression: var offsetArg }, { Expression: var lengthArg }])
            return;

        var ct = context.CancellationToken;
        var arrayLength = TryGetArrayLength(arrayArg, context.SemanticModel, ct);
        if (arrayLength is null) return;
        if (context.SemanticModel.GetConstantValue(offsetArg, ct).Value is not int offset) return;
        if (context.SemanticModel.GetConstantValue(lengthArg, ct).Value is not int length) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Convert") return;

        if (offset >= 0 && length >= 0 && offset + length <= arrayLength) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, offset, length, arrayLength));
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
