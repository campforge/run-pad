using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 149, batch) — same theme/rationale as CultureSensitiveStringCompareAnalyzer
// (RPAD0005, pass 145) extended to the other single-string-argument String instance methods that
// are ALSO culture-sensitive by default per .NET's own documentation: StartsWith(string)/
// EndsWith(string)/IndexOf(string) all compare using CultureInfo.CurrentCulture unless a
// StringComparison is passed explicitly — the same well-known "Turkish I" class of gotcha.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CultureSensitiveStringMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0031";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Culture-sensitive string method",
        messageFormat: "'{0}(string)' uses the current culture — results can differ across machines/locales; specify a StringComparison explicitly",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "StartsWith/EndsWith/IndexOf's single-string-argument overloads compare using CultureInfo.CurrentCulture, not ordinal comparison.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetMethodNames =
        ImmutableHashSet.Create("StartsWith", "EndsWith", "IndexOf");

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
                IsStatic: false,
                ContainingType.SpecialType: SpecialType.System_String,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
            })
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }
}
