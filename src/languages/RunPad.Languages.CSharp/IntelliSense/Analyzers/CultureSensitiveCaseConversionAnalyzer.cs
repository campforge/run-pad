using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Same theme as CultureSensitiveStringCompareAnalyzer
// (RPAD0005)/CultureSensitiveStringMethodAnalyzer (RPAD0031), extended to the no-argument
// `ToUpper()`/`ToLower()` overloads — culture-sensitive by default, and literally THE textbook example
// of the "Turkish I" bug (in the Turkish locale, "i".ToUpper() gives "İ", not "I"). ToUpperInvariant()/
// ToLowerInvariant() (or an explicit CultureInfo overload) is the fix.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CultureSensitiveCaseConversionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0039";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Culture-sensitive case conversion",
        messageFormat: "'{0}()' uses the current culture — use '{0}Invariant()' unless you specifically need culture-aware casing",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The parameterless ToUpper()/ToLower() overloads compare using CultureInfo.CurrentCulture — the classic 'Turkish I' bug.");

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
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            return;
        if (!TargetMethodNames.Contains(methodName)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol
            {
                IsStatic: false,
                ContainingType.SpecialType: SpecialType.System_String,
                Parameters.Length: 0,
            })
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }
}
