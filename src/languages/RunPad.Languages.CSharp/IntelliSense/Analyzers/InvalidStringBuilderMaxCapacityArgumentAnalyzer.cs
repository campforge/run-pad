using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. A correctness
// finding on `new StringBuilder(int capacity, int maxCapacity)`: confirmed empirically before writing
// this that it throws ArgumentOutOfRangeException not just for a negative `capacity` (already covered
// by NegativeCapacityArgumentAnalyzer, RPAD0231) but ALSO for a non-negative `capacity` when
// `maxCapacity < 1` or `capacity > maxCapacity` — e.g. `new StringBuilder(5, 3)` (capacity exceeds the
// cap) or `new StringBuilder(1, 0)` (maxCapacity itself must be at least 1). Deliberately scoped to
// exclude the already-covered negative-capacity case (`capacity < 0` is left entirely to RPAD0231, no
// double-reporting on the same call) — this analyzer only reports the two conditions RPAD0231 doesn't
// already check. Scoped to LITERAL capacity/maxCapacity only, same reasoning as the rest of this
// project's negative-literal family.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidStringBuilderMaxCapacityArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0273";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "StringBuilder capacity/maxCapacity combination always throws",
        messageFormat: "'new StringBuilder({0}, {1})' always throws ArgumentOutOfRangeException at runtime — maxCapacity must be at least 1, and capacity cannot exceed maxCapacity",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The StringBuilder(int capacity, int maxCapacity) constructor throws ArgumentOutOfRangeException when maxCapacity is less than 1, or capacity exceeds maxCapacity.");

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
        if (creation.ArgumentList is not { Arguments: [{ Expression: var capacityArg }, { Expression: var maxCapacityArg }] })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(capacityArg, ct).Value is not int capacity) return;
        if (context.SemanticModel.GetConstantValue(maxCapacityArg, ct).Value is not int maxCapacity) return;
        if (capacity < 0) return;
        if (maxCapacity >= 1 && capacity <= maxCapacity) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Text.StringBuilder") return;
        if (ctor.Parameters is not [{ Name: "capacity" }, { Name: "maxCapacity" }]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), capacity, maxCapacity));
    }
}
