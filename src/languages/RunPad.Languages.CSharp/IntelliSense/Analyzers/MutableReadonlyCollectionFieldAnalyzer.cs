using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 159, batch) — own pick, not a Roslynator port. `readonly` on a
// field only prevents the FIELD ITSELF from being reassigned — it says nothing about the mutability of
// whatever the field's value points to. `public static readonly List<int> Items = new();` reads like
// "safe to expose", but every caller can still call `.Add`/`.Clear`/`.RemoveAt` on it. Deliberately
// scoped to non-private accessibility only (public/protected/internal/protected internal) — a private
// mutable collection field is the normal, correct way to hold encapsulated mutable state and is not
// this analyzer's business.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MutableReadonlyCollectionFieldAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0064";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Publicly-accessible readonly field exposes a mutable collection",
        messageFormat: "'{0}' is declared readonly, but its contents can still be mutated by callers — consider an IReadOnlyList/IReadOnlyDictionary/ImmutableArray instead",
        category: "Reliability",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "readonly only prevents reassigning the field itself; it does nothing to stop callers from mutating a collection's contents.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> MutableCollectionTypeNames = ImmutableHashSet.Create(
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.Queue`1",
        "System.Collections.Generic.Stack`1",
        "System.Collections.ObjectModel.Collection`1");

    // ToDisplayString() on a generic type's OriginalDefinition renders type parameter names
    // ("List<T>"), not the metadata-name form ("List`1") the set above is keyed on — building the
    // qualified metadata name by hand (namespace + MetadataName, which already includes the arity
    // suffix) is what actually matches.
    private static string QualifiedMetadataName(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? $"{ns.ToDisplayString()}.{type.MetadataName}"
            : type.MetadataName;

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.FieldDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var field = (FieldDeclarationSyntax)context.Node;
        if (!field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)) return;
        if (!HasNonPrivateAccessibility(field.Modifiers)) return;

        var fieldType = context.SemanticModel.GetTypeInfo(field.Declaration.Type, context.CancellationToken).Type;
        if (!IsMutableCollectionType(fieldType)) return;

        foreach (var variable in field.Declaration.Variables)
            context.ReportDiagnostic(Diagnostic.Create(Rule, variable.Identifier.GetLocation(), variable.Identifier.Text));
    }

    private static bool HasNonPrivateAccessibility(SyntaxTokenList modifiers) =>
        modifiers.Any(SyntaxKind.PublicKeyword) || modifiers.Any(SyntaxKind.ProtectedKeyword) || modifiers.Any(SyntaxKind.InternalKeyword);

    private static bool IsMutableCollectionType(ITypeSymbol? type)
    {
        if (type is IArrayTypeSymbol) return true;
        if (type is not INamedTypeSymbol named) return false;
        return MutableCollectionTypeNames.Contains(QualifiedMetadataName(named.OriginalDefinition));
    }
}
