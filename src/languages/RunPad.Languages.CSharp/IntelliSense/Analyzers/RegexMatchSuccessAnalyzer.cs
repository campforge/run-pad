using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят третий проход, batch) — own pick, not a Roslynator port.
// `Regex.Match(input, pattern).Success` (or the instance form, `regex.Match(input).Success`) builds a
// full Match object — capture groups, indices, the works — just to read one bool off it.
// `Regex.IsMatch(input, pattern)`/`regex.IsMatch(input)` answers the same question without allocating
// or populating any of that.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RegexMatchSuccessAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0080";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use Regex.IsMatch instead of Match(...).Success",
        messageFormat: "Use 'IsMatch(...)' instead of 'Match(...).Success' — IsMatch doesn't need to build the Match/capture data",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Match(...).Success builds a full Match object (groups, captures, indices) just to read one bool; IsMatch(...) answers the same question directly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;
        if (memberAccess.Name.Identifier.Text != "Success") return;
        if (memberAccess.Expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Match" },
            } matchInvocation)
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(matchInvocation, ct).Symbol is not IMethodSymbol matchMethod) return;
        if (matchMethod.ContainingType?.ToDisplayString() != "System.Text.RegularExpressions.Regex") return;

        if (context.SemanticModel.GetSymbolInfo(memberAccess, ct).Symbol is not IPropertySymbol successProperty) return;
        // Success is actually declared on Group (Match : Group), not on Match itself.
        if (successProperty.ContainingType?.ToDisplayString() != "System.Text.RegularExpressions.Group") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.GetLocation()));
    }
}
