using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as NegativeCapacityArgumentAnalyzer (RPAD0231): `new
// IPEndPoint(IPAddress address, int port)` (and the `long address` overload) throws
// ArgumentOutOfRangeException when `port` falls outside the valid `[0, 65535]` range — confirmed
// empirically via scratch `dotnet run` script.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidIPEndPointConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0349";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "IPEndPoint port argument is out of range",
        messageFormat: "'new IPEndPoint(...)' always throws ArgumentOutOfRangeException at runtime — port ({0}) must be between 0 and 65535",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new IPEndPoint(address, port) throws ArgumentOutOfRangeException when port is outside the valid [0, 65535] range.");

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
        if (creation.ArgumentList is not { Arguments: [_, { Expression: var portArg }] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(portArg, ct).Value is not int port || (port >= 0 && port <= 65535))
            return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Net.IPEndPoint") return;
        if (ctor.Parameters is not [_, { Type.SpecialType: SpecialType.System_Int32 }]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), port));
    }
}
