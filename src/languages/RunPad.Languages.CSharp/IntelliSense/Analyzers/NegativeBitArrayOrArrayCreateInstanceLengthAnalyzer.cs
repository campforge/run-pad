using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "negative
// literal argument always throws" theme as NegativeCapacityArgumentAnalyzer (RPAD0231), grouping two
// genuinely unrelated APIs that happen to share the exact mechanism (single negative `int length`
// literal, `ArgumentOutOfRangeException`) confirmed empirically via scratch `dotnet run` script: the
// `System.Collections.BitArray(int length)` constructor, and the static
// `System.Array.CreateInstance(Type elementType, int length)` method. Grouped into one analyzer class —
// same precedent as other "single negative int literal, unrelated receivers" groupings in this catalog
// — rather than two near-duplicate classes differing only in syntax kind (constructor vs static method).
// Scoped to the simplest 2-argument `Array.CreateInstance` overload only — the multi-dimensional-array
// overloads (taking an `int[]` of lengths, or separate lengths/lowerBounds arrays) are a different,
// more involved shape not attempted here.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeBitArrayOrArrayCreateInstanceLengthAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0308";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Negative BitArray/Array.CreateInstance length always throws",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — length cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new BitArray(int length) and Array.CreateInstance(Type, int length) both throw ArgumentOutOfRangeException when length is negative.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var lengthArg }] }) return;
        if (!TryGetNegativeIntValue(lengthArg, out _)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.Parameters is not [{ Name: "length", Type.SpecialType: SpecialType.System_Int32 }]) return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Collections.BitArray") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), creation.ToString()));
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "CreateInstance" })
            return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var lengthArg }]) return;
        if (!TryGetNegativeIntValue(lengthArg, out _)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { IsStatic: true, Name: "CreateInstance" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Array") return;
        if (method.Parameters is not [_, { Name: "length", Type.SpecialType: SpecialType.System_Int32 }]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString()));
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
