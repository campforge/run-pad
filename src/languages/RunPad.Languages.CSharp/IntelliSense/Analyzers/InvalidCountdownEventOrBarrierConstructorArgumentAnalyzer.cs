using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "constructor argument validation" theme as InvalidSemaphoreConstructorArgumentAnalyzer (RPAD0322,
// this same проход): `new CountdownEvent(int initialCount)` and `new Barrier(int
// participantCount[, Action<Barrier>? postPhaseAction])` both throw ArgumentOutOfRangeException for a
// negative count/participant argument — confirmed empirically via scratch `dotnet run` script (both
// error messages even use the identical "must be a non-negative value" wording), a genuine EXACT
// formula match unlike the Semaphore/SemaphoreSlim pair (which differ in exception type for the
// upper-bound case) — so this is one analyzer for both types rather than two, matching this project's
// precedent of sharing an analyzer across types/overloads with identical validation formulas (e.g.
// RPAD0272's ToBase64String/ToHexString/ToHexStringLower). `Barrier`'s optional 2nd argument
// (`postPhaseAction`) doesn't affect validity and is simply ignored.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCountdownEventOrBarrierConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0323";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "CountdownEvent/Barrier constructor argument is negative",
        messageFormat: "'new {0}(...)' always throws ArgumentOutOfRangeException at runtime — {1} cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new CountdownEvent(initialCount) and new Barrier(participantCount[, postPhaseAction]) throw ArgumentOutOfRangeException when the count argument is negative.");

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
        if (args[0].Expression is not { } countArg) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int count || count >= 0) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.Parameters is not [{ Type.SpecialType: SpecialType.System_Int32 }, ..]) return;

        var (typeName, parameterName) = ctor.ContainingType?.ToDisplayString() switch
        {
            "System.Threading.CountdownEvent" when args.Count == 1 => ("CountdownEvent", "initialCount"),
            "System.Threading.Barrier" when args.Count is 1 or 2 => ("Barrier", "participantCount"),
            _ => (null, null),
        };
        if (typeName is null) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), typeName, parameterName));
    }
}
