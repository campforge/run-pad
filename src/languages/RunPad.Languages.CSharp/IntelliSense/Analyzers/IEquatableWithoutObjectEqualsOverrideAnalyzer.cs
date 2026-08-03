using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести семидесятый проход, batch) — own pick, not a Roslynator port. A correctness
// finding: implementing `IEquatable<T>.Equals(T)` on a class or struct WITHOUT also overriding
// `object.Equals(object)` leaves the type with two different, disagreeing notions of equality — generic
// code (`EqualityComparer<T>.Default`, `List<T>.Contains`, `==` on `IEquatable<T>` constraints, etc.)
// calls the fast, correct `Equals(T)` overload, while non-generic code (boxed comparisons, `ArrayList`,
// `object.Equals(x, y)`, LINQ methods that internally use `object.Equals`) falls through to the
// inherited, unrelated `object.Equals(object)` — reference equality for a class, or the slow
// reflection-based default for a struct — silently giving a DIFFERENT answer than `Equals(T)` would.
// Confirmed via PROBE that nothing else in this project or the IDE's own diagnostics already flags this
// (the compiler's own CS0659 only checks the OPPOSITE direction — override Equals without override
// GetHashCode — not this one). Scoped to `class`/`struct` declarations that directly list `IEquatable<T>`
// in their own base list (not `record`/`record struct`, which the compiler already gives a correct,
// consistent `object.Equals(object)` override to automatically, whether or not `IEquatable<T>` is
// explicitly written) and don't themselves declare an `override bool Equals(object)` — the rare case
// where a BASE class already supplies a correct override is accepted as an unlikely false-positive risk,
// consistent with this project's other declaration-scoped (not full-inheritance-chain) analyzers.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IEquatableWithoutObjectEqualsOverrideAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0262";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "IEquatable<T> implemented without overriding object.Equals",
        messageFormat: "'{0}' implements IEquatable<T>.Equals but doesn't override object.Equals(object) — non-generic code (boxed comparisons, object.Equals, some LINQ methods) will silently use a different, inconsistent notion of equality",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A type that implements IEquatable<T> but doesn't also override object.Equals(object) has two disagreeing notions of equality: generic code uses the fast, correct Equals(T), while non-generic code silently falls back to the inherited object.Equals.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var declaration = (TypeDeclarationSyntax)context.Node;
        if (declaration.BaseList is null) return;

        var ct = context.CancellationToken;
        var implementsIEquatable = declaration.BaseList.Types.Any(baseType =>
            context.SemanticModel.GetTypeInfo(baseType.Type, ct).Type is INamedTypeSymbol namedType
            && namedType.OriginalDefinition.ToDisplayString() == "System.IEquatable<T>");
        if (!implementsIEquatable) return;

        if (context.SemanticModel.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol type) return;

        var hasObjectEqualsOverride = type.GetMembers("Equals").OfType<IMethodSymbol>().Any(m =>
            m.IsOverride && m.Parameters is [{ Type.SpecialType: SpecialType.System_Object }]);
        if (hasObjectEqualsOverride) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, declaration.Identifier.GetLocation(), declaration.Identifier.Text));
    }
}
