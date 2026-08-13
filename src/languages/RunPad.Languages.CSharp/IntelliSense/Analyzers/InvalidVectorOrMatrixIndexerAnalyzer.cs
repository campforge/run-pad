using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста семьдесят седьмой проход, batch) — own pick, not a Roslynator port; candidate
// surfaced by a research subagent survey of BCL Parse/indexer families not yet covered by this catalog,
// then independently confirmed empirically (scratch `dotnet run` script) before implementing, same
// discipline as every other analyzer here. `System.Numerics.Vector2`/`Vector3`/`Vector4`/`Quaternion`'s
// `this[int]` indexer, and `Matrix3x2`/`Matrix4x4`'s `this[int, int]` indexer, throw
// `ArgumentOutOfRangeException` for an index outside a FIXED, per-type bound baked into the type itself
// (Vector2: [0,1], Vector3: [0,2], Vector4/Quaternion: [0,3], Matrix3x2: row [0,2]/col [0,1], Matrix4x4:
// row/col [0,3]) — confirmed for every type/bound individually, not assumed from the research summary
// alone. Genuinely simpler in scope than this catalog's other "literal receiver" indexer analyzers
// (RPAD0283/RPAD0303/RPAD0357/etc.): because the bound is a property of the TYPE, not of the receiver's
// runtime VALUE, the receiver does NOT need to be a literal construction at all — `new Vector2(x, y)[2]`,
// a `Vector2`-typed parameter indexed the same way, a field, a method-call result, all throw identically
// regardless of the actual component values, so this analyzer only needs to confirm the receiver's
// STATIC TYPE, never its literal contents.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidVectorOrMatrixIndexerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0370";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Index is out of range for a Vector/Matrix type",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — {1} only has valid indices {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Vector2/Vector3/Vector4/Quaternion's this[int] indexer, and Matrix3x2/Matrix4x4's this[int, int] indexer, throw ArgumentOutOfRangeException for an index outside the type's own fixed valid range.");

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
        var ct = context.CancellationToken;
        if (context.SemanticModel.GetTypeInfo(elementAccess.Expression, ct).Type is not { } receiverType) return;

        var typeName = receiverType.ToDisplayString();
        switch (typeName)
        {
            case "System.Numerics.Vector2":
                CheckSingleIndex(context, elementAccess, typeName, "Vector2", 1);
                return;
            case "System.Numerics.Vector3":
                CheckSingleIndex(context, elementAccess, typeName, "Vector3", 2);
                return;
            case "System.Numerics.Vector4":
                CheckSingleIndex(context, elementAccess, typeName, "Vector4", 3);
                return;
            case "System.Numerics.Quaternion":
                CheckSingleIndex(context, elementAccess, typeName, "Quaternion", 3);
                return;
            case "System.Numerics.Matrix3x2":
                CheckDoubleIndex(context, elementAccess, typeName, "Matrix3x2", 2, 1);
                return;
            case "System.Numerics.Matrix4x4":
                CheckDoubleIndex(context, elementAccess, typeName, "Matrix4x4", 3, 3);
                return;
        }
    }

    private static void CheckSingleIndex(SyntaxNodeAnalysisContext context, ElementAccessExpressionSyntax elementAccess, string typeName, string shortName, int maxIndex)
    {
        if (elementAccess.ArgumentList.Arguments is not [{ Expression: var indexArg }]) return;
        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(indexArg, ct).Value is not int index) return;
        if (index >= 0 && index <= maxIndex) return;

        if (context.SemanticModel.GetSymbolInfo(elementAccess, ct).Symbol is not IPropertySymbol { IsIndexer: true, ContainingType: { } containingType }
            || containingType.ToDisplayString() != typeName)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), elementAccess.ToString(), shortName, $"[0, {maxIndex}]"));
    }

    private static void CheckDoubleIndex(SyntaxNodeAnalysisContext context, ElementAccessExpressionSyntax elementAccess, string typeName, string shortName, int maxRow, int maxCol)
    {
        if (elementAccess.ArgumentList.Arguments is not [{ Expression: var rowArg }, { Expression: var colArg }]) return;
        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(rowArg, ct).Value is not int row) return;
        if (context.SemanticModel.GetConstantValue(colArg, ct).Value is not int col) return;
        if (row >= 0 && row <= maxRow && col >= 0 && col <= maxCol) return;

        if (context.SemanticModel.GetSymbolInfo(elementAccess, ct).Symbol is not IPropertySymbol { IsIndexer: true, ContainingType: { } containingType }
            || containingType.ToDisplayString() != typeName)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), elementAccess.ToString(), shortName, $"row [0, {maxRow}] and column [0, {maxCol}]"));
    }
}
