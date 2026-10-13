using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as InvalidMemoryStreamConstructorArgumentAnalyzer (RPAD0335):
// `memoryStream.Read(byte[] buffer, int offset, int count)` and `memoryStream.Write(byte[]
// buffer, int offset, int count)` on a LITERAL array throw ArgumentOutOfRangeException for a negative
// `offset`/`count` or when `offset + count` exceeds the buffer's length — confirmed empirically via
// scratch `dotnet run` script. Deliberately scoped to `ContainingType == "System.IO.MemoryStream"`
// rather than the base `System.IO.Stream` — confirmed via a throwaway Roslyn symbol-resolution check
// (not assumed) that `MemoryStream` OVERRIDES `Read`/`Write` directly with its own implementation, so a
// call through a `MemoryStream`-typed receiver resolves to `MemoryStream`'s own symbol, not `Stream`'s
// shared base implementation; validation for other stream types (`FileStream`, etc.) hasn't been
// verified and is intentionally left uncovered rather than assumed identical.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidStreamReadOrWriteArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0336";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "MemoryStream.Read/Write arguments are out of range",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — offset ({1}) and count ({2}) don't fit within a {3}-element array",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "MemoryStream.Read(buffer, offset, count) and MemoryStream.Write(buffer, offset, count) throw ArgumentOutOfRangeException when offset or count is negative, or offset + count exceeds the buffer's length.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Read" or "Write" } access)
            return;
        var methodName = access.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var bufferArg }, { Expression: var offsetArg }, { Expression: var countArg }])
            return;

        var ct = context.CancellationToken;
        var arrayLength = TryGetArrayLength(bufferArg, context.SemanticModel, ct);
        if (arrayLength is not { } length) return;
        if (context.SemanticModel.GetConstantValue(offsetArg, ct).Value is not int offset) return;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int count) return;
        if (offset >= 0 && count >= 0 && offset + count <= length) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.IO.MemoryStream") return;
        if (method.Parameters is not [_, { Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }])
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), offset, count, length));
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
