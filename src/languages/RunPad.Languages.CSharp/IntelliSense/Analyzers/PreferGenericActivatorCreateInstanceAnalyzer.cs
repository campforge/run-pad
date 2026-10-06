using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто восемьдесят девятый проход, batch) — own pick, not a Roslynator port. Same
// "generic overload avoids the Type/object round trip" theme as PreferGenericMarshalSizeOfAnalyzer
// (RPAD0176, this same проход). `Activator.CreateInstance(typeof(T))` boxes through `Type` and
// returns a plain `object?` that then needs casting — `Activator.CreateInstance<T>()` (available since
// this project's SDK floor) does the same construction and returns `T` directly, strongly typed.
// Scoped to the argument-less constructor overload only — `Activator.CreateInstance(typeof(T), args)`
// has no generic equivalent that accepts constructor arguments this simply.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferGenericActivatorCreateInstanceAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0177";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use the generic Activator.CreateInstance<T>() overload",
        messageFormat: "Use 'Activator.CreateInstance<T>()' instead of 'Activator.CreateInstance(typeof(T))' — avoids the Type round trip and returns T directly",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Activator.CreateInstance(typeof(T)) looks up a Type object and returns object; the generic overload constructs T directly and returns it strongly typed.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "CreateInstance" }) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: TypeOfExpressionSyntax }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "CreateInstance", IsGenericMethod: false } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Activator") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
