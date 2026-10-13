using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// The char counterpart to
// CultureSensitiveCaseConversionAnalyzer (RPAD0039, pass 151), which only covers string's INSTANCE
// ToUpper()/ToLower() — System.Char's equivalents are STATIC (`char.ToUpper(c)`/`char.ToLower(c)`), a
// different call shape entirely, so RPAD0039's `ContainingType.SpecialType == System_String` guard
// never matches them. Same underlying bug class either way: the single-argument overload compares
// using CultureInfo.CurrentCulture — the classic "Turkish I" trap. char.ToUpperInvariant(c)/
// char.ToLowerInvariant(c) (or the explicit-CultureInfo overload) is the fix.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CultureSensitiveCharCaseConversionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0068";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Culture-sensitive char case conversion",
        messageFormat: "'char.{0}(...)' uses the current culture — use 'char.{0}Invariant(...)' unless you specifically need culture-aware casing",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The single-argument char.ToUpper(c)/char.ToLower(c) overloads compare using CultureInfo.CurrentCulture — the classic 'Turkish I' bug.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetMethodNames = ImmutableHashSet.Create("ToUpper", "ToLower");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count != 1) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            return;
        if (!TargetMethodNames.Contains(methodName)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol
            {
                IsStatic: true,
                ContainingType.SpecialType: SpecialType.System_Char,
                Parameters.Length: 1,
            })
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }
}
