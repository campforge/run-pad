using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `new Random(Environment.TickCount)` is a pre-.NET-Core idiom for getting a "different sequence each
// run" — but the parameterless `Random()` constructor has used a proper random seed source (not
// time-based) since .NET Core, so explicitly seeding with TickCount is now strictly worse: it's lower
// entropy, and two Random instances constructed within the same clock tick (a common enough occurrence
// in a tight loop) get the IDENTICAL seed and produce the IDENTICAL sequence — a real, if subtle, bug
// source that plain `new Random()` doesn't have.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantRandomSeedAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0095";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant Random(Environment.TickCount) seed",
        messageFormat: "Use 'new Random()' instead of 'new Random(Environment.TickCount)' — the parameterless constructor already uses a proper random seed, and TickCount can collide across near-simultaneous calls",
        category: "Reliability",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Random()'s parameterless constructor has used a real random seed source since .NET Core — seeding with Environment.TickCount is lower-entropy and can collide within the same clock tick.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList?.Arguments is not
            [{ Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "TickCount" } tickCountAccess }])
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol ctor) return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Random") return;

        if (context.SemanticModel.GetSymbolInfo(tickCountAccess, ct).Symbol is not IPropertySymbol tickCountProperty)
            return;
        if (tickCountProperty.ContainingType?.ToDisplayString() != "System.Environment") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
    }
}
