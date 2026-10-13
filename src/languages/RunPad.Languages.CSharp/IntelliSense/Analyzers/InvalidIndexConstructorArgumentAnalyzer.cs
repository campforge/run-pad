using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "constructor argument validation" theme as InvalidCountdownEventOrBarrierConstructorArgumentAnalyzer
// (RPAD0323, проход 350): `new System.Index(int value[, bool fromEnd])` throws
// ArgumentOutOfRangeException for a negative `value` — confirmed empirically via scratch `dotnet run`
// script. Scoped to the explicit `new Index(...)` constructor call only — the implicit `int`-to-`Index`
// conversion operator (`Index idx = -1;`) performs the identical validation internally but isn't a
// constructor call and is out of scope for this analyzer, consistent with this catalog's existing
// precedent of only following explicit constructor/method syntax rather than every code path that
// eventually reaches the same runtime check.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidIndexConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0327";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Index constructor value is negative",
        messageFormat: "'new Index(...)' always throws ArgumentOutOfRangeException at runtime — value cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new Index(value[, fromEnd]) throws ArgumentOutOfRangeException when value is negative.");

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
        if (creation.ArgumentList is not { Arguments: { Count: 1 or 2 } args }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(args[0].Expression, ct).Value is not int value || value >= 0)
            return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Index") return;
        if (ctor.Parameters is not [{ Type.SpecialType: SpecialType.System_Int32 }, ..]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
    }
}
