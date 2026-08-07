using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста сорок шестой проход, batch) — own pick, not a Roslynator port. Same "run the real
// validator" theme as InvalidDateTimeConstructorArgumentAnalyzer (RPAD0264) — applied here to the
// `AddYears`/`AddMonths`/`AddDays`/`AddHours`/`AddMinutes`/`AddSeconds`/`AddMilliseconds`/
// `AddMicroseconds`/`AddTicks` instance methods on `DateTime` (and `AddYears`/`AddMonths`/`AddDays` on
// `DateOnly`): calling one of these on a value that would push the result outside the representable
// range (before year 1 or after year 9999) throws ArgumentOutOfRangeException — confirmed empirically
// via scratch `dotnet run` script, not assumed. Rather than hand-rolling calendar arithmetic to predict
// the overflow, this analyzer constructs the REAL receiver from a literal `new DateTime(...)`/`new
// DateOnly(...)` call and invokes the REAL `AddXxx` method on it inside a try/catch — the same
// "construct the real thing and see what happens" approach used throughout this catalog, which
// correctly handles the one genuinely surprising edge case found during PROBE: `new DateTime(9999, 12,
// 31).AddTicks(1)` does NOT throw (there's still tick-level headroom before the end of that day), while
// `.AddDays(1)`/`.AddHours(2)` on the same receiver DO — a hand-written "is this the max year" check
// would have gotten `AddTicks` wrong. Scoped to the receiver being a literal `new DateTime(...)`/`new
// DateOnly(...)` object-creation expression (all-literal-int constructor arguments, same extraction as
// RPAD0264) and a single literal numeric argument to the `AddXxx` call.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DateTimeOrDateOnlyAddMethodOverflowAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0313";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "DateTime/DateOnly Add method always overflows",
        messageFormat: "'{0}.{1}({2})' always throws ArgumentOutOfRangeException at runtime — the result is outside the representable range",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Calling an AddXxx method on a literal DateTime/DateOnly with an argument that pushes the result before year 1 or after year 9999 throws ArgumentOutOfRangeException.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> DateTimeAddMethods = ImmutableHashSet.Create(
        "AddYears", "AddMonths", "AddDays", "AddHours", "AddMinutes", "AddSeconds", "AddMilliseconds", "AddMicroseconds", "AddTicks");

    private static readonly ImmutableHashSet<string> DateOnlyAddMethods = ImmutableHashSet.Create(
        "AddYears", "AddMonths", "AddDays");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Expression: ObjectCreationExpressionSyntax { ArgumentList: { Arguments: var ctorArgs } } creation,
            } access)
        {
            return;
        }
        var methodName = access.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var addArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        var receiverTypeName = ctor.ContainingType?.ToDisplayString();
        var isDateTime = receiverTypeName == "System.DateTime" && DateTimeAddMethods.Contains(methodName);
        var isDateOnly = receiverTypeName == "System.DateOnly" && DateOnlyAddMethods.Contains(methodName);
        if (!isDateTime && !isDateOnly) return;
        if (!ctor.Parameters.All(p => p.Type.SpecialType == SpecialType.System_Int32)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedMethodName } method
            || resolvedMethodName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != receiverTypeName) return;

        var ctorValues = new int[ctorArgs.Count];
        for (var i = 0; i < ctorArgs.Count; i++)
        {
            if (context.SemanticModel.GetConstantValue(ctorArgs[i].Expression, ct).Value is not int v) return;
            ctorValues[i] = v;
        }

        if (context.SemanticModel.GetConstantValue(addArg, ct) is not { HasValue: true, Value: { } addConstantValue }) return;

        bool alwaysThrows;
        if (isDateTime)
        {
            if (ToDoubleOrLong(method, addConstantValue) is not { } addValue) return;
            alwaysThrows = DateTimeAlwaysThrows(ctorValues, methodName, addValue);
        }
        else
        {
            if (addConstantValue is not int addIntValue) return;
            alwaysThrows = DateOnlyAlwaysThrows(ctorValues, methodName, addIntValue);
        }
        if (!alwaysThrows) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), receiverTypeName == "System.DateTime" ? "DateTime" : "DateOnly", methodName, addConstantValue));
    }

    private static object? ToDoubleOrLong(IMethodSymbol method, object constantValue)
    {
        if (method.Parameters is not [{ Type.SpecialType: var paramType }]) return null;
        return (paramType, constantValue) switch
        {
            (SpecialType.System_Double, double d) => d,
            (SpecialType.System_Double, int i) => (double)i,
            (SpecialType.System_Int32, int i) => i,
            (SpecialType.System_Int64, int i) => (long)i,
            (SpecialType.System_Int64, long l) => l,
            _ => null,
        };
    }

    private static bool DateTimeAlwaysThrows(int[] ctorValues, string methodName, object addValue)
    {
        try
        {
            var dt = ctorValues.Length switch
            {
                3 => new DateTime(ctorValues[0], ctorValues[1], ctorValues[2]),
                6 => new DateTime(ctorValues[0], ctorValues[1], ctorValues[2], ctorValues[3], ctorValues[4], ctorValues[5]),
                7 => new DateTime(ctorValues[0], ctorValues[1], ctorValues[2], ctorValues[3], ctorValues[4], ctorValues[5], ctorValues[6]),
                _ => (DateTime?)null,
            };
            if (dt is not { } value) return false;

            _ = methodName switch
            {
                "AddYears" => value.AddYears((int)addValue),
                "AddMonths" => value.AddMonths((int)addValue),
                "AddDays" => value.AddDays((double)addValue),
                "AddHours" => value.AddHours((double)addValue),
                "AddMinutes" => value.AddMinutes((double)addValue),
                "AddSeconds" => value.AddSeconds((double)addValue),
                "AddMilliseconds" => value.AddMilliseconds((double)addValue),
                "AddMicroseconds" => value.AddMicroseconds((double)addValue),
                _ => value.AddTicks((long)addValue),
            };
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    private static bool DateOnlyAlwaysThrows(int[] ctorValues, string methodName, int addValue)
    {
        if (ctorValues.Length != 3) return false;
        try
        {
            var value = new DateOnly(ctorValues[0], ctorValues[1], ctorValues[2]);
            _ = methodName switch
            {
                "AddYears" => value.AddYears(addValue),
                "AddMonths" => value.AddMonths(addValue),
                _ => value.AddDays(addValue),
            };
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
