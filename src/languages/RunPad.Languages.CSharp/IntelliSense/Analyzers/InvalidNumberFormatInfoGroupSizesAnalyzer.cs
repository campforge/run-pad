using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (449-й проход, batch) — candidate #3 from the eleventh research subagent survey (deferred
// at проход 448 for its different, array-literal argument shape), implemented here after independent
// re-verification. `NumberFormatInfo.NumberGroupSizes`/`CurrencyGroupSizes`/`PercentGroupSizes` (all
// `int[]`) — sibling of RPAD0439's scalar `int` pattern properties on the same type. Confirmed empirically,
// identical rule across all three properties: every element must be 1-9, EXCEPT the last element, which
// may additionally be 0 (a trailing 0 means "no further grouping past this point" — a genuine, valid
// terminator, not a malformed value) — a 0 anywhere else, or any element outside 1-9 (including negative),
// throws `ArgumentException`. Confirmed: `new[] { 3, 0, 2 }` throws (0 not last), `new[] { 3, 0 }` is
// valid, `new[] { -1 }` throws, an empty array is valid (no grouping at all), `new[] { 9, 9, 9 }` is valid.
// Confirmed invariant across en-US/de-DE/ar-SA/ja-JP (pure structural check, no culture data involved). No
// side effects. Scoped to a LITERAL array whose every element is itself a constant — either an explicit
// initializer (`new[] { ... }`/`new int[] { ... }`) or a C# 12 collection expression (`[ ... ]`) — same
// "literal, not an arbitrary expression" scoping already established for `InvalidGuidByteArrayLengthAnalyzer`
// (RPAD0266), just extracting each element's own constant VALUE here rather than only the array's length.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidNumberFormatInfoGroupSizesAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0440";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "NumberFormatInfo group-sizes array is invalid",
        messageFormat: "'NumberFormatInfo.{0} = ...' always throws ArgumentException at runtime — every element must be between 1 and 9, except the last element, which may also be 0",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "NumberFormatInfo.NumberGroupSizes/CurrencyGroupSizes/PercentGroupSizes throw ArgumentException for an int[] containing an element outside 1-9, unless that element is the last one and is 0.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> PropertyNames = ImmutableHashSet.Create(
        "NumberGroupSizes", "CurrencyGroupSizes", "PercentGroupSizes");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: var propertyName } access)
            return;
        if (!PropertyNames.Contains(propertyName)) return;

        var ct = context.CancellationToken;
        if (TryGetConstantIntArray(assignment.Right, context.SemanticModel, ct) is not { } values) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var symbolName } property)
            return;
        if (symbolName != propertyName) return;
        if (property.Type is not IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Int32 }) return;

        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Globalization.NumberFormatInfo") return;

        if (!AlwaysThrows(propertyName, values)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), propertyName));
    }

    private static int[]? TryGetConstantIntArray(ExpressionSyntax expression, SemanticModel semanticModel, System.Threading.CancellationToken ct)
    {
        IReadOnlyList<ExpressionSyntax>? exprs = expression switch
        {
            ArrayCreationExpressionSyntax { Initializer: { } init } => init.Expressions,
            ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInit } => implicitInit.Expressions,
            CollectionExpressionSyntax collectionExpr when collectionExpr.Elements.All(e => e is ExpressionElementSyntax) =>
                [.. collectionExpr.Elements.Cast<ExpressionElementSyntax>().Select(e => e.Expression)],
            _ => null,
        };
        if (exprs is null) return null;

        var values = new int[exprs.Count];
        for (var i = 0; i < exprs.Count; i++)
        {
            if (semanticModel.GetConstantValue(exprs[i], ct).Value is not int v) return null;
            values[i] = v;
        }
        return values;
    }

    private static bool AlwaysThrows(string propertyName, int[] values)
    {
        var info = new NumberFormatInfo();
        try
        {
            switch (propertyName)
            {
                case "NumberGroupSizes": info.NumberGroupSizes = values; break;
                case "CurrencyGroupSizes": info.CurrencyGroupSizes = values; break;
                case "PercentGroupSizes": info.PercentGroupSizes = values; break;
            }
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
