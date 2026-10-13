using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// The switch-EXPRESSION counterpart to
// NonExhaustiveEnumSwitchAnalyzer (RPAD0048), which covers switch STATEMENTS (the
// compiler already flags a genuinely non-exhaustive switch EXPRESSION with no discard via CS8509, so
// that half doesn't need its own analyzer) — this one is the opposite direction: a `_ => ...` discard
// arm that's actually unreachable because every enum member is ALREADY covered by an explicit,
// simple constant pattern arm before it. Deliberately conservative: bails out entirely on `when`
// guards, non-constant patterns, or [Flags] enums, same reasoning as RPAD0048.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantDiscardArmInExhaustiveSwitchExpressionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0059";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Unreachable discard arm in an already-exhaustive switch expression",
        messageFormat: "This '_' arm is unreachable — every member of the enum is already handled by an earlier arm",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A discard arm after every enum member is already covered by explicit case arms can never be reached.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SwitchExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var switchExpr = (SwitchExpressionSyntax)context.Node;
        var ct = context.CancellationToken;

        if (context.SemanticModel.GetTypeInfo(switchExpr.GoverningExpression, ct).Type is not { TypeKind: TypeKind.Enum } enumType)
            return;
        if (enumType.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"))
            return;

        SwitchExpressionArmSyntax? discardArm = null;
        var handled = new HashSet<string>();

        foreach (var arm in switchExpr.Arms)
        {
            if (arm.WhenClause is not null) return; // a `when` guard could change reachability — don't guess

            if (arm.Pattern is DiscardPatternSyntax)
            {
                if (discardArm is not null) return;
                discardArm = arm;
                continue;
            }

            if (arm.Pattern is not ConstantPatternSyntax { Expression: { } expr }) return; // any other pattern kind — too hard to judge safely, bail
            if (context.SemanticModel.GetSymbolInfo(expr, ct).Symbol is not IFieldSymbol field
                || !SymbolEqualityComparer.Default.Equals(field.ContainingType, enumType))
            {
                return;
            }

            handled.Add(field.Name);
        }

        if (discardArm is null) return;

        var allMembers = enumType.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.ConstantValue is not null).Select(f => f.Name).ToHashSet();
        if (allMembers.Count > 0 && allMembers.IsSubsetOf(handled))
            context.ReportDiagnostic(Diagnostic.Create(Rule, discardArm.GetLocation()));
    }
}
