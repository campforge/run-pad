using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (трёхсотый проход, batch) — own pick, not a Roslynator port. Same "literal receiver, known
// length" theme as StringLiteralIndexerOutOfRangeAnalyzer (RPAD0282, проход 299), applied to a freshly
// created array literal instead of a string literal: `new[] { 1, 2, 3 }[10]` throws
// IndexOutOfRangeException when the index is >= the initializer's own known element count — confirmed
// empirically (not assumed) that the C# compiler does NOT catch this itself. Deliberately restricted to a
// NON-NEGATIVE literal index — a negative literal index (`new[] { 1, 2, 3 }[-1]`) is already caught by the
// compiler's own CS0251 warning ("Indexing an array with a negative index"), confirmed via the same
// scratch script, so that half of the theme is already covered and out of scope here, unlike
// StringLiteralIndexerOutOfRangeAnalyzer (whose receiver is a string literal, not subject to CS0251, which
// only applies to actual arrays).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ArrayLiteralIndexerOutOfRangeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0283";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Indexer is out of range for an array literal",
        messageFormat: "Indexing this array literal with [{0}] always throws IndexOutOfRangeException at runtime — it has only {1} element(s)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Indexing a freshly created array literal with a non-negative index greater than or equal to its own element count throws IndexOutOfRangeException.");

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
        var initializer = elementAccess.Expression switch
        {
            ArrayCreationExpressionSyntax { Initializer: { } explicitInit } => explicitInit,
            ImplicitArrayCreationExpressionSyntax implicitCreation => implicitCreation.Initializer,
            _ => null,
        };
        if (initializer is null) return;
        if (elementAccess.ArgumentList.Arguments is not [{ Expression: var indexArg }]) return;
        if (indexArg is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } indexLiteral)
            return;
        if (indexLiteral.Token.Value is not int index || index < 0) return;

        var elementCount = initializer.Expressions.Count;
        if (index < elementCount) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), index, elementCount));
    }
}
