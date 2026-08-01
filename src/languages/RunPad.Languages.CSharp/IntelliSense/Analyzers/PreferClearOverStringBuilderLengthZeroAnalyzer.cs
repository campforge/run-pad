using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести третий проход, batch) — own pick, not a Roslynator port.
// `sb.Length = 0` truncates a StringBuilder back to empty by reaching into its Length property
// directly — `sb.Clear()` says the same intent directly and is what the type's own API was designed
// for, without relying on the reader knowing that setting Length to zero is how you empty one.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferClearOverStringBuilderLengthZeroAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0191";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Clear() over setting StringBuilder.Length to zero",
        messageFormat: "'sb.Length = 0' can be replaced with 'sb.Clear()' — Clear() says the intent directly instead of relying on the Length-zero convention",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "StringBuilder.Clear() truncates the builder back to empty, exactly like setting Length to 0, but names the intent directly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Length", Expression: var receiver })
            return;

        var ct = context.CancellationToken;
        var rightConstant = context.SemanticModel.GetConstantValue(assignment.Right, ct);
        if (rightConstant.Value is not 0) return;

        if (context.SemanticModel.GetSymbolInfo(assignment.Left, ct).Symbol is not IPropertySymbol { Name: "Length" } lengthProperty)
            return;
        if (lengthProperty.ContainingType?.ToDisplayString() != "System.Text.StringBuilder") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation()));
    }
}
