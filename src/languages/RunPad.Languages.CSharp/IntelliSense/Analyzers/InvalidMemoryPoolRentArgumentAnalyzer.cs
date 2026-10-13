using System.Buffers;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Buffers.MemoryPool<T>.Shared.Rent(int
// minimumBufferSize)` — an instance method on the static `Shared` singleton (confirmed via reflection:
// the `Shared` property getter is static, `Rent` itself is an instance method). Unusual, precisely
// verified boundary: `-1` is a SENTINEL meaning "use the default size" and does NOT throw (confirmed —
// returns a 4096-byte buffer); any value `<= -2` down to `int.MinValue` throws
// `ArgumentOutOfRangeException`. The naive `< 0` predicate a careless implementation would reach for is
// WRONG — the correct predicate is `< -1`, confirmed via boundary testing (-1 no throw, -2 throws,
// -100 throws, int.MinValue throws; 0 and 1 also don't throw). Confirmed no `[SupportedOSPlatform]`
// gating. Side-effect-safe: renting from the shared array-backed pool and disposing immediately is the
// same accepted probing pattern already used for the catalog's other `ArrayPool`-style analyzers — no
// real I/O, just a pooled array checkout/return. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP
// (pure numeric check, no culture data involved).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidMemoryPoolRentArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0482";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "MemoryPool.Rent minimumBufferSize is invalid",
        messageFormat: "'MemoryPool<...>.Shared.Rent({0})' always throws ArgumentOutOfRangeException at runtime — minimumBufferSize must be -1 (the default) or non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "MemoryPool<T>.Shared.Rent(int) throws ArgumentOutOfRangeException for any value less than -1 (-1 itself is a valid sentinel meaning \"use the default size\").");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Rent" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var sizeArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(sizeArg, ct).Value is not int minimumBufferSize) return;
        if (minimumBufferSize >= -1) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Rent",
                ContainingType: { } containingType,
                Parameters: [{ Type.SpecialType: SpecialType.System_Int32 }],
            })
        {
            return;
        }
        if (containingType.OriginalDefinition.ToDisplayString() != "System.Buffers.MemoryPool<T>") return;

        try
        {
            MemoryPool<byte>.Shared.Rent(minimumBufferSize).Dispose();
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), minimumBufferSize));
        }
    }
}
