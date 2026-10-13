using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as NegativeCapacityArgumentAnalyzer (RPAD0231): `string.Create
// <TState>(int length, TState state, SpanAction<char, TState> action)` throws
// ArgumentOutOfRangeException for a negative `length` — confirmed empirically via scratch `dotnet run`
// script.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeStringCreateLengthArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0339";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "string.Create length argument is negative",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — length cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "string.Create(length, state, action) throws ArgumentOutOfRangeException when length is negative.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Create" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var lengthArg }, _, _]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(lengthArg, ct).Value is not int length || length >= 0)
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Create" } method)
            return;
        if (method.ContainingType?.SpecialType != SpecialType.System_String) return;
        if (method.Parameters is not [{ Type.SpecialType: SpecialType.System_Int32 }, _, _]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString()));
    }
}
