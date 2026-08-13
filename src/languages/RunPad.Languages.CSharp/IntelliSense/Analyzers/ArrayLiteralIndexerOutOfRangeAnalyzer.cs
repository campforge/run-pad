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
// Extended in the триста шестьдесят шестой проход to also handle the `^N` Index-form single-element
// indexer (`new[] { 1, 2, 3 }[^5]`), the shape this analyzer's own doc comment previously didn't mention
// at all and sibling StringLiteralIndexerOutOfRangeAnalyzer explicitly calls out as deferred — resolved
// to a plain offset (`elementCount - N`) and fed through the SAME bounds check/exception type
// (`IndexOutOfRangeException`), confirmed empirically (scratch `dotnet run` script) including the
// easy-to-miss `^0` case (means "one past the end", always out of range for any receiver, empty or not —
// confirmed throwing). Unlike the plain-literal branch, this one does NOT defer negative RESOLVED offsets
// to the compiler's CS0251 — confirmed via a real `dotnet build` on the same snippet that CS0251 (or any
// other diagnostic) does NOT fire for `^N` forms at all, negative-resolved-offset or not, so there's no
// existing coverage to defer to here.
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

        var elementCount = initializer.Expressions.Count;

        if (indexArg is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } indexLiteral)
        {
            if (indexLiteral.Token.Value is not int index || index < 0 || index < elementCount) return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), index, elementCount));
            return;
        }

        if (indexArg is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.IndexExpression, Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } fromEndLiteral })
        {
            if (fromEndLiteral.Token.Value is not int fromEndValue) return;
            var resolvedIndex = elementCount - fromEndValue;
            if (resolvedIndex >= 0 && resolvedIndex < elementCount) return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), $"^{fromEndValue}", elementCount));
        }
    }
}
