using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; last of the four
// "needs verification" candidates from the research subagent's ranked list (проход 377), confirmed
// empirically before implementing — the one that needed the deepest extra verification pass, done in this
// проход before writing any code. Same "run the real validator" theme, applied to
// `System.Numerics.Complex.Parse(string, IFormatProvider)` (the generic-math `INumberBase<Complex>.Parse`
// overload — `Complex` has no bare `Parse(string)`, unlike most other numeric types; deliberately NOT the
// 3-arg `Parse(string, NumberStyles, IFormatProvider)` overload, same single-shape restraint as this
// catalog's other analyzers). GENUINE SURPRISE, confirmed empirically across ~20 varied malformed inputs
// (missing/mismatched brackets, wrong separator, empty components, extra components, non-bracketed input,
// whitespace) BEFORE implementing: every single one throws `OverflowException`, never the more obviously-
// named `FormatException` one might expect for a parse failure. Doubly confirmed this name is genuinely
// misleading, not just unexpected: an actual numeric MAGNITUDE overflow in a component (e.g. "<1e400; 2>",
// 400+ nines) does NOT throw at all — it parses successfully to `<∞; 2>`, since the underlying `double`
// parse saturates to `PositiveInfinity` rather than failing. So `OverflowException` here is thrown for
// structural/syntax failures, not for numeric overflow — confirmed this is nonetheless a single, CONSISTENT
// exception type for every syntactically-malformed shape tried, safe to catch specifically rather than
// broadly. Valid syntax is flexible: "<1;2>" (no space), "< 1 ; 2 >" (extra whitespace), "<+1; +2>",
// "<NaN; 2>", "<Infinity; 2>" all parse successfully — confirmed empirically before picking "not flagged"
// test literals, following this session's established discipline around not assuming validity.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidComplexParseLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0378";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Complex.Parse literal is not a valid Complex string",
        messageFormat: "'Complex.Parse(...)' always throws OverflowException at runtime — '{0}' is not a valid Complex string (expected the \"<real; imaginary>\" format)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Complex.Parse(string, IFormatProvider) throws OverflowException (not FormatException) for a string that isn't a valid \"<real; imaginary>\"-shaped Complex representation.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var valueArg }, _]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(valueArg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Parse",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }, _],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Numerics.Complex") return;

        try
        {
            _ = Complex.Parse(value, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
        }
    }
}
