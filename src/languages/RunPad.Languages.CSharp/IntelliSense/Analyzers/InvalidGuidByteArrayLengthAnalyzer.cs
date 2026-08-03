using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести семьдесят седьмой проход, batch) — own pick, not a Roslynator port. A correctness
// finding: `new Guid(byte[])` requires an array of EXACTLY 16 bytes, and `new Guid(int, short, short,
// byte[])` requires the trailing byte array to be EXACTLY 8 bytes — both throw `ArgumentException` for
// any other length (confirmed empirically before writing this). Scoped to a LITERAL array whose length
// is known at the call site — either an explicit initializer (`new byte[] { ... }`) or an explicit,
// constant size with no initializer (`new byte[N]`) — same "literal, not an arbitrary expression"
// scoping as the rest of this project's negative-literal family, just applied to array length instead
// of an int's sign.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidGuidByteArrayLengthAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0266";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Guid byte array constructor argument has the wrong length",
        messageFormat: "'new Guid(...)' always throws ArgumentException at runtime — the byte array must have exactly {0} elements, but this one has {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new Guid(byte[]) requires exactly 16 bytes, and new Guid(int, short, short, byte[]) requires the trailing byte array to have exactly 8 bytes.");

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
        if (creation.ArgumentList is not { Arguments.Count: 1 or 4 } argumentList) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Guid") return;
        if (ctor.Parameters is not [.., { Type: IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte } }])
            return;

        var expectedLength = argumentList.Arguments.Count == 1 ? 16 : 8;
        var arrayArg = argumentList.Arguments[^1].Expression;

        var actualLength = TryGetArrayLength(arrayArg, context.SemanticModel, ct);
        if (actualLength is null || actualLength == expectedLength) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), expectedLength, actualLength));
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
