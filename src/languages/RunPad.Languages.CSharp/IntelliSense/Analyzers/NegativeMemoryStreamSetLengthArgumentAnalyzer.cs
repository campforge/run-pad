using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста пятьдесят третий проход, batch) — own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as NegativeArrayResizeOrClearArgumentAnalyzer (RPAD0263):
// `memoryStream.SetLength(long value)` throws ArgumentOutOfRangeException for a negative `value` —
// confirmed empirically via scratch `dotnet run` script. Scoped to `ContainingType ==
// "System.IO.MemoryStream"` — `Stream.SetLength` is ABSTRACT (each stream type supplies its own
// implementation, unlike `Read`/`Write`, which have a shared, non-abstract base implementation as of
// .NET Core 2.1+), so this check intentionally does not generalize to other stream types without
// separately confirming each one's own validation behavior.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeMemoryStreamSetLengthArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0338";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "MemoryStream.SetLength argument is negative",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — length cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "MemoryStream.SetLength(value) throws ArgumentOutOfRangeException when value is negative.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "SetLength" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var valueArg }]) return;

        var ct = context.CancellationToken;
        var constant = context.SemanticModel.GetConstantValue(valueArg, ct);
        var isNegative = constant.Value switch
        {
            long l => l < 0,
            int i => i < 0,
            _ => (bool?)null,
        };
        if (isNegative is not true) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "SetLength" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.IO.MemoryStream") return;
        if (method.Parameters is not [{ Type.SpecialType: SpecialType.System_Int64 }]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString()));
    }
}
