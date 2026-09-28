using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста сорок девятый проход, batch) — own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as ListLiteralIndexerOutOfRangeAnalyzer (RPAD0303, проход 341), applied
// to `System.Collections.BitArray` instead of `List<T>`: `new BitArray(N)[index]` throws
// ArgumentOutOfRangeException when `index` is negative or `>= N`, for a LITERAL constructor length `N`
// and a literal `index` — confirmed empirically via scratch `dotnet run` script, not assumed. Unlike
// `List<T>`/array literals, `BitArray` has no collection-initializer syntax — its known length comes
// from the constructor's own literal `length` argument instead, a genuinely different literal-length
// extraction shape than the rest of this catalog's "literal receiver" family.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BitArrayLiteralIndexerOutOfRangeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0321";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Index is out of range for a BitArray literal",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — this BitArray has only {1} element(s)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Indexing a freshly created BitArray(length) with an index outside its own known length throws ArgumentOutOfRangeException.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ElementAccessExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var elementAccess = (ElementAccessExpressionSyntax)context.Node;
        if (elementAccess.Expression is not BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: [{ Expression: var lengthArg }] } creation)
            return;
        if (elementAccess.ArgumentList.Arguments is not [{ Expression: var indexArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(lengthArg, ct).Value is not int length) return;
        if (context.SemanticModel.GetConstantValue(indexArg, ct).Value is not int index) return;
        if (index >= 0 && index < length) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Collections.BitArray") return;
        if (ctor.Parameters is not [{ Name: "length", Type.SpecialType: SpecialType.System_Int32 }]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), elementAccess.ToString(), length));
    }
}
