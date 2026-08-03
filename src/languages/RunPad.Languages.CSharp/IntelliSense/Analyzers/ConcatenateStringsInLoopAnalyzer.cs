using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 149, batch) — idea ported from Roslynator's
// UseStringBuilderInsteadOfConcatenationInLoopAnalyzer-style rules (Apache License 2.0,
// https://github.com/dotnet/roslynator). `s += x;` inside a loop reallocates and copies the ENTIRE
// accumulated string on every iteration (strings are immutable) — O(n²) total work for what looks
// like an O(n) loop. A well-known, real performance trap, not a style nitpick.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConcatenateStringsInLoopAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0033";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "String concatenation in a loop",
        messageFormat: "Concatenating a string in a loop reallocates and copies the whole accumulated " +
            "string every iteration — consider a StringBuilder instead",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Strings are immutable — 's += x' inside a loop is O(n) work per iteration, O(n²) total, for what looks like an O(n) loop.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.AddAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        var type = context.SemanticModel.GetTypeInfo(assignment.Left, context.CancellationToken).Type;
        if (type is not { SpecialType: SpecialType.System_String }) return;

        for (var node = assignment.Parent; node is not null; node = node.Parent)
        {
            if (node is ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForEachStatementSyntax)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation()));
                return;
            }

            // A lambda/local function/method boundary means we've left the loop's own control flow.
            if (node is MethodDeclarationSyntax or LocalFunctionStatementSyntax
                or LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
            {
                return;
            }
        }
    }
}
