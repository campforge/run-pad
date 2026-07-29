using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок седьмой проход, batch) — idea ported from Roslynator's
// UseCountOrLengthPropertyInsteadOfAnyMethodAnalyzer (Apache License 2.0,
// https://github.com/dotnet/roslynator). `collection.Any()` (the parameterless LINQ overload) forces
// an IEnumerator allocation and a MoveNext() call just to answer a question a `Count`/`Length`
// property already knows for free — LINQ-hygiene theme, fitting for a LINQPad-descended scratchpad.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseCountOrLengthPropertyInsteadOfAnyMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0015";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use Count/Length property instead of Any() method",
        messageFormat: "Use the '{0}' property instead of 'Any()' — avoids an unnecessary enumerator allocation",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A collection's Count/Length property already knows the answer Any() computes by enumerating.");

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
        if (invocation.ArgumentList.Arguments.Count != 0) return; // the predicate-less overload only
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Any" } memberAccess)
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression, context.CancellationToken).Type;
        if (receiverType is null) return;

        if (FindCountOrLengthProperty(receiverType) is { } propertyName)
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), propertyName));
    }

    private static string? FindCountOrLengthProperty(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol) return "Length";

        if (HasCountOrLengthProperty(type, out var ownName)) return ownName;

        foreach (var iface in type.AllInterfaces)
        {
            if (HasCountOrLengthProperty(iface, out var ifaceName)) return ifaceName;
        }

        return null;
    }

    private static bool HasCountOrLengthProperty(ITypeSymbol type, out string name)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is IPropertySymbol { Type.SpecialType: SpecialType.System_Int32 } property
                && property.Name is "Count" or "Length")
            {
                name = property.Name;
                return true;
            }
        }

        name = "";
        return false;
    }
}
