using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "negative
// literal argument always throws" theme as NegativeCapacityArgumentAnalyzer (RPAD0231, constructor form),
// but on a genuinely different SYNTACTIC shape: `list.Capacity = -1` (a property ASSIGNMENT, not a
// constructor or method call) also throws ArgumentOutOfRangeException — confirmed empirically via scratch
// `dotnet run` script, not assumed. Scoped to `List<T>` specifically — confirmed via reflection (not
// assumed) that `Dictionary<TKey,TValue>`/`HashSet<T>`/`Stack<T>`/`Queue<T>`'s own `Capacity` properties
// are all READ-ONLY (get-only), so this assignment shape simply doesn't exist for them; `List<T>` is the
// only one of this family with a settable `Capacity`. Scoped to a LITERAL negative integer only, same
// reasoning as the rest of this family.
// Extended in the триста двадцать четвёртый проход to also cover `StringBuilder.Length = -1` and
// `System.IO.MemoryStream.Position = -1` — same mechanism (negative literal assigned to a settable
// property, ArgumentOutOfRangeException), confirmed empirically to throw identically, generalized from a
// single hardcoded (type, property) check into a small lookup table. `MemoryStream.Position` is
// `long`-typed (unlike `List<T>.Capacity`'s `int`), but a plain digit literal without an `L` suffix is
// still parsed as an `int`-valued token even when implicitly converted to `long` at the assignment site,
// so the existing literal-extraction helper works unchanged. Class name/`DiagnosticId` kept as-is despite
// the scope growing beyond `List<T>.Capacity`, same precedent as other same-mechanism extensions in this
// catalog.
// Extended again in the триста сорок девятый проход to also cover `list.Capacity = N` where the
// receiver is itself a literal `new List<T> { ... }` collection initializer and `N` (non-negative) is
// still LESS than the initializer's own known element count — confirmed empirically that this throws
// the identical `ArgumentOutOfRangeException` (capacity can never be shrunk below the current element
// count). Confirmed this does NOT apply to `StringBuilder.Length`/`MemoryStream.Position` — setting
// either of those to a smaller-but-non-negative value simply truncates/seeks rather than throwing, so
// this new check is scoped to `List<T>.Capacity` specifically. The message format is now parameterized
// with a reason string to describe either condition.
// Extended again (450-й проход, candidate #7 from the twelfth research subagent survey) to also cover
// `System.IO.MemoryStream.Capacity = -1` — confirmed empirically as a genuine gap in the lookup table
// below (`MemoryStream` was only mapped to `Position`, never `Capacity`), same negative-literal-throws
// mechanism, identical for both an expandable (`new MemoryStream()`) and a fixed-external-buffer
// (`new MemoryStream(byte[])`) stream. Deliberately does NOT extend to a POSITIVE `Capacity` value that's
// merely smaller than a fixed-buffer stream's current size (that failure mode is receiver-state-dependent,
// not literal-invariant — same class of exclusion already documented above for `List<T>.Capacity`'s
// separate element-count check, which is intentionally its own distinct condition, not generalized here).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeListCapacityAssignmentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0293";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Invalid property assignment always throws",
        messageFormat: "'{0}.{1} = {2}' always throws ArgumentOutOfRangeException at runtime — {3}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Assigning a negative value to List<T>.Capacity, StringBuilder.Length, or System.IO.MemoryStream.Position throws ArgumentOutOfRangeException; assigning a List<T>.Capacity value less than the list's current element count also throws.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, ImmutableHashSet<string>> TargetPropertiesByTypeName =
        ImmutableDictionary.CreateRange(
            [
                KeyValuePair.Create("System.Collections.Generic.List<T>", ImmutableHashSet.Create("Capacity")),
                KeyValuePair.Create("System.Text.StringBuilder", ImmutableHashSet.Create("Length")),
                KeyValuePair.Create("System.IO.MemoryStream", ImmutableHashSet.Create("Position", "Capacity")),
            ]);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax access) return;
        var propertyName = access.Name.Identifier.Text;

        var ct = context.CancellationToken;
        string reason;
        if (TryGetNegativeLiteralValue(assignment.Right, out var literalText))
        {
            reason = $"{propertyName} cannot be negative";
        }
        else if (propertyName == "Capacity"
            && context.SemanticModel.GetConstantValue(assignment.Right, ct).Value is int newCapacity
            && newCapacity >= 0
            && TryGetListLiteralElementCount(access.Expression, context.SemanticModel, ct) is { } elementCount
            && newCapacity < elementCount)
        {
            literalText = newCapacity.ToString();
            reason = $"Capacity ({newCapacity}) cannot be less than the list's current Count ({elementCount})";
        }
        else
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var resolvedName } property
            || resolvedName != propertyName)
        {
            return;
        }
        var containingTypeName = property.ContainingType?.OriginalDefinition.ToDisplayString();
        if (containingTypeName is null
            || !TargetPropertiesByTypeName.TryGetValue(containingTypeName, out var validProperties)
            || !validProperties.Contains(propertyName))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), access.Expression.ToString(), propertyName, literalText, reason));
    }

    private static int? TryGetListLiteralElementCount(ExpressionSyntax expression, SemanticModel semanticModel, System.Threading.CancellationToken ct)
    {
        InitializerExpressionSyntax? initializer = expression switch
        {
            ObjectCreationExpressionSyntax { Initializer: { } explicitInit } => explicitInit,
            ImplicitObjectCreationExpressionSyntax { Initializer: { } implicitInit } => implicitInit,
            _ => null,
        };
        if (initializer is null || initializer.Kind() != SyntaxKind.CollectionInitializerExpression) return null;
        if (semanticModel.GetTypeInfo(expression, ct).Type is not { } type
            || type.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.List<T>")
        {
            return null;
        }
        return initializer.Expressions.Count;
    }

    private static bool TryGetNegativeLiteralValue(ExpressionSyntax expression, out string literalText)
    {
        literalText = "";
        if (expression is not PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.UnaryMinusExpression,
                Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal,
            })
        {
            return false;
        }

        literalText = "-" + literal.Token.ValueText;
        return true;
    }
}
