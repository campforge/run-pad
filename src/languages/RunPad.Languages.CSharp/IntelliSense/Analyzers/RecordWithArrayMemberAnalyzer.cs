using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. A correctness
// finding: arrays don't override Equals/GetHashCode (they use the default reference-identity
// implementation from object), so a record with an array-typed member has its compiler-generated
// equality compare that member BY REFERENCE, not by value — two records with equal array CONTENTS are
// NOT equal, defeating the entire point of choosing `record` over `class` in the first place. Confirmed
// empirically before writing this: not just positional-parameter array members but ALSO plain
// auto-implemented properties declared directly in the record body participate in the compiler-generated
// equality the same way, so both shapes are covered.
// Extended (двести шестьдесят девятый проход, batch) to also cover List<T>/Dictionary<TKey,TValue>/
// HashSet<T>/Queue<T>/Stack<T> — the identical underlying gotcha, none of them override Equals either.
// Deliberately scoped to these specific CONCRETE BCL types only, matched by generic-type-definition, not
// to interface-typed members (IList<T>/IReadOnlyList<T>/IEnumerable<T>/etc.) — an interface-typed member
// could resolve to a genuinely value-equal concrete type at runtime (ImmutableArray<T>,
// ImmutableList<T>), and flagging based on the static interface type alone would risk false positives
// there.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RecordWithArrayMemberAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0261";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Record has a member whose type breaks value equality",
        messageFormat: "'{0}' member of record '{1}' is {2} — it doesn't override Equals/GetHashCode, so the record's generated equality compares it by reference, not by value",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Arrays and the standard mutable BCL collection types (List<T>, Dictionary<TKey,TValue>, HashSet<T>, Queue<T>, Stack<T>) don't override Equals/GetHashCode, so a record with a member of one of these types compares that member by reference in the compiler-generated equality — two records with equal contents will not be equal.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> ReferenceEqualityCollectionTypes = ImmutableHashSet.Create(
        "System.Collections.Generic.List<T>",
        "System.Collections.Generic.Dictionary<TKey, TValue>",
        "System.Collections.Generic.HashSet<T>",
        "System.Collections.Generic.Queue<T>",
        "System.Collections.Generic.Stack<T>");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.RecordDeclaration, SyntaxKind.RecordStructDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var record = (RecordDeclarationSyntax)context.Node;
        var ct = context.CancellationToken;

        if (record.ParameterList is not null)
        {
            foreach (var parameter in record.ParameterList.Parameters)
            {
                if (parameter.Type is null) continue;
                var description = DescribeIfReferenceEqualityType(context.SemanticModel.GetTypeInfo(parameter.Type, ct).Type);
                if (description is null) continue;
                context.ReportDiagnostic(Diagnostic.Create(Rule, parameter.Identifier.GetLocation(), parameter.Identifier.Text, record.Identifier.Text, description));
            }
        }

        foreach (var member in record.Members)
        {
            if (member is not PropertyDeclarationSyntax { AccessorList.Accessors: var accessors } property) continue;
            if (accessors.Any(a => a.Body is not null || a.ExpressionBody is not null)) continue;
            var propertyDescription = DescribeIfReferenceEqualityType(context.SemanticModel.GetTypeInfo(property.Type, ct).Type);
            if (propertyDescription is null) continue;

            context.ReportDiagnostic(Diagnostic.Create(Rule, property.Identifier.GetLocation(), property.Identifier.Text, record.Identifier.Text, propertyDescription));
        }
    }

    private static string? DescribeIfReferenceEqualityType(ITypeSymbol? type)
    {
        if (type is IArrayTypeSymbol) return "an array type";
        if (type is INamedTypeSymbol namedType && ReferenceEqualityCollectionTypes.Contains(namedType.OriginalDefinition.ToDisplayString()))
            return $"a {namedType.OriginalDefinition.Name}";
        return null;
    }
}
