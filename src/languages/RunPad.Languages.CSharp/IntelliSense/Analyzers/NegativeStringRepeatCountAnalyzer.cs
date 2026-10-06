using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести сорок второй проход, batch) — own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as NegativeElementAtIndexAnalyzer (RPAD0236, same batch): `new
// string(c, count)` is documented to throw `ArgumentOutOfRangeException` when `count` is negative.
// Scoped to a LITERAL negative integer, same reasoning as the rest of this family.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeStringRepeatCountAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0237";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "new string(char, count) with a negative count always throws ArgumentOutOfRangeException",
        messageFormat: "'new string(c, {0})' always throws ArgumentOutOfRangeException at runtime — count cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The string(char, int) constructor throws ArgumentOutOfRangeException when count is negative.");

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
        if (creation.ArgumentList is not { Arguments: [_, { Expression: var countArg }] }) return;
        if (!TryGetNegativeIntValue(countArg, out var value)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.SpecialType != SpecialType.System_String) return;
        if (ctor.Parameters is not [{ Type.SpecialType: SpecialType.System_Char }, { Type.SpecialType: SpecialType.System_Int32 }])
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), value));
    }

    private static bool TryGetNegativeIntValue(ExpressionSyntax expression, out int value)
    {
        value = 0;
        if (expression is not PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.UnaryMinusExpression,
                Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal,
            })
        {
            return false;
        }
        if (literal.Token.Value is not int positiveValue) return false;

        value = -positiveValue;
        return true;
    }
}
