using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "constructor/method argument validation" theme as InvalidBlockingCollectionConstructorArgumentAnalyzer
// (RPAD0333): `memoryStream.CopyTo(Stream destination, int bufferSize)` throws
// ArgumentOutOfRangeException when `bufferSize` isn't strictly positive — confirmed empirically via
// scratch `dotnet run` script. Scoped to `ContainingType == "System.IO.MemoryStream"` — confirmed via a
// throwaway Roslyn symbol-resolution check (not assumed, same caution as the sibling
// InvalidStreamReadOrWriteArgumentAnalyzer/RPAD0336) that `MemoryStream` overrides `CopyTo` directly
// rather than using the base `Stream` implementation, so a call through a `MemoryStream`-typed receiver
// resolves to `MemoryStream`'s own symbol.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeStreamCopyToBufferSizeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0337";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "MemoryStream.CopyTo bufferSize argument is not positive",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — bufferSize must be positive",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "MemoryStream.CopyTo(destination, bufferSize) throws ArgumentOutOfRangeException when bufferSize isn't strictly positive.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "CopyTo" })
            return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var bufferSizeArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(bufferSizeArg, ct).Value is not int bufferSize || bufferSize >= 1)
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "CopyTo" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.IO.MemoryStream") return;
        if (method.Parameters is not [_, { Type.SpecialType: SpecialType.System_Int32 }]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString()));
    }
}
