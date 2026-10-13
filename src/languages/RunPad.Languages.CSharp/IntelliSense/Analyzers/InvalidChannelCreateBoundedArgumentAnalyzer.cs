using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "literal argument that always throws" theme as InvalidBlockingCollectionConstructorArgumentAnalyzer
// (RPAD0333, проход 352): `Channel.CreateBounded<T>(int capacity)` throws ArgumentOutOfRangeException
// for a negative `capacity` — confirmed empirically via scratch `dotnet run` script. Unlike
// `BlockingCollection`'s `boundedCapacity` (which must be strictly positive), `Channel.CreateBounded`'s
// `capacity` of exactly `0` is itself VALID (a zero-capacity bounded channel, meaning every write must
// rendezvous with a pending read) — confirmed rather than assumed, a genuinely different boundary than
// the superficially similar `BlockingCollection` case.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidChannelCreateBoundedArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0351";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Channel.CreateBounded capacity is negative",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — capacity cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Channel.CreateBounded<T>(capacity) throws ArgumentOutOfRangeException when capacity is negative.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "CreateBounded" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var capacityArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(capacityArg, ct).Value is not int capacity || capacity >= 0)
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "CreateBounded" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Threading.Channels.Channel") return;
        if (method.Parameters is not [{ Type.SpecialType: SpecialType.System_Int32 }]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString()));
    }
}
