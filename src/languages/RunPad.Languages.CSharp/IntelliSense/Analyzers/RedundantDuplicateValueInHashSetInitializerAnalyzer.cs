using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. A redundancy
// sibling to DuplicateKeyInDictionaryInitializerAnalyzer (RPAD0241), but for a different
// outcome: `new HashSet<T> { a, a }` desugars to two `Add` calls — unlike Dictionary.Add, HashSet<T>.Add
// does NOT throw for a value already present, it just returns false and does nothing — so the repeated
// element compiles fine and runs fine, but is pure dead weight (and, like the Dictionary case, probably
// a copy-paste mistake where the second value was meant to be something else). Info/Redundancy rather
// than Warning/Reliability, since nothing actually breaks. Also probed alongside a duplicate OBJECT-
// initializer property assignment (`new Foo { X = 1, X = 2 }`) idea, which was dropped — the compiler/
// IDE already emits "Duplicate initialization of member 'X'" for that shape, so there was nothing left
// for a new analyzer to add there.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantDuplicateValueInHashSetInitializerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0242";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Duplicate value in a HashSet collection initializer",
        messageFormat: "This value duplicates an earlier entry in the same initializer — HashSet<T>.Add silently does nothing for a value already present, so this entry has no effect",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "HashSet<T>.Add returns false and does nothing when the value is already in the set, so a repeated literal in a collection initializer is pure dead weight.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.Initializer is not { RawKind: (int)SyntaxKind.CollectionInitializerExpression } initializer)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetTypeInfo(creation, ct).Type is not INamedTypeSymbol type) return;
        if (type.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.HashSet<T>") return;

        var seenValues = new Dictionary<object, bool>();
        foreach (var element in initializer.Expressions)
        {
            var constant = context.SemanticModel.GetConstantValue(element, ct);
            if (!constant.HasValue || constant.Value is null) continue;

            if (!seenValues.TryAdd(constant.Value, true))
                context.ReportDiagnostic(Diagnostic.Create(Rule, element.GetLocation()));
        }
    }
}
