using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Sibling of
// InvalidCountdownEventOrBarrierConstructorArgumentAnalyzer (RPAD0323, проход 350) — same two types,
// but their INSTANCE methods instead of their constructors: `CountdownEvent.Signal(int
// signalCount)`/`AddCount(int signalCount)` and `Barrier.AddParticipants(int
// participantCount)`/`RemoveParticipants(int participantCount)` all throw ArgumentOutOfRangeException
// when the argument isn't strictly positive — confirmed empirically via scratch `dotnet run` script
// that all four share the identical "must be positive" formula (unlike the constructors, which allow
// zero — this is a genuinely different boundary, confirmed rather than assumed).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCountdownEventOrBarrierParticipantCountAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0353";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "CountdownEvent/Barrier participant count argument is not positive",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — the argument must be positive",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "CountdownEvent.Signal/AddCount and Barrier.AddParticipants/RemoveParticipants throw ArgumentOutOfRangeException when the argument isn't strictly positive.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, string> TargetTypeByMethod = ImmutableDictionary.CreateRange(
        [
            KeyValuePair.Create("Signal", "System.Threading.CountdownEvent"),
            KeyValuePair.Create("AddCount", "System.Threading.CountdownEvent"),
            KeyValuePair.Create("AddParticipants", "System.Threading.Barrier"),
            KeyValuePair.Create("RemoveParticipants", "System.Threading.Barrier"),
        ]);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            return;
        if (!TargetTypeByMethod.TryGetValue(methodName, out var expectedType)) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var countArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int count || count >= 1)
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != expectedType) return;
        if (method.Parameters is not [{ Type.SpecialType: SpecialType.System_Int32 }]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString()));
    }
}
