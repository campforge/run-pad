using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок шестой проход) — idea ported (not copied — own implementation, see
// EmptyCatchClauseAnalyzer's own doc comment for the same rationale) from Roslynator's
// RemoveUnusedMemberDeclarationAnalyzer (RCS1213, Apache License 2.0,
// https://github.com/dotnet/roslynator), narrowed to private fields and methods only (the two shapes
// with the least ambiguity — no interface/override/event-handler-delegate concerns a private property
// or event can run into). Verified empirically before writing this (PROBE tests, since removed) that
// neither the compiler's own CS0169 ("field is never used") nor any built-in analyzer currently
// surfaces this for RunPad's AdhocWorkspace-based live analysis — a genuine gap, not a duplicate of
// existing coverage (unlike, say, "introduce local variable", which turned out to already be covered
// by a built-in CodeRefactoringProvider — see TODO.md's own note on проход 142).
//
// Deliberately does NOT flag: anything with an attribute (could be reached via reflection/
// serialization/DI — conservative), constructors/destructors/operators/indexers (different syntax
// node shapes entirely, naturally excluded by only matching FieldDeclarationSyntax/
// MethodDeclarationSyntax), properties/events (left for a later, more careful pass — see TODO.md).
// A member used ONLY by itself (pure self-recursion with no other call site) is a known, accepted
// false negative — usage is detected by a plain "does any identifier in the containing type resolve
// to this symbol" search, which a self-reference inside the member's own body satisfies. Preferring a
// missed detection over a wrong one, same principle as every other analyzer in this batch.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnusedPrivateMemberAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0006";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Unused private member",
        messageFormat: "Private member '{0}' is never used",
        category: "Reliability",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A private field or method that's never referenced anywhere in its containing " +
            "type is dead code — often a leftover from an earlier edit.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeTypeDeclaration, SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration);
    }

    private static void AnalyzeTypeDeclaration(SyntaxNodeAnalysisContext context)
    {
        var typeDeclaration = (TypeDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;
        var ct = context.CancellationToken;

        var candidates = new Dictionary<ISymbol, SyntaxToken>(SymbolEqualityComparer.Default);
        foreach (var member in typeDeclaration.Members)
        {
            if (member is FieldDeclarationSyntax { AttributeLists.Count: 0 } field && IsPrivate(field.Modifiers))
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    if (semanticModel.GetDeclaredSymbol(variable, ct) is IFieldSymbol fieldSymbol)
                        candidates[fieldSymbol] = variable.Identifier;
                }
            }
            else if (member is MethodDeclarationSyntax { AttributeLists.Count: 0 } method
                && IsPrivate(method.Modifiers) && !method.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                if (semanticModel.GetDeclaredSymbol(method, ct) is IMethodSymbol methodSymbol)
                    candidates[methodSymbol] = method.Identifier;
            }
        }

        if (candidates.Count == 0) return;

        var used = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var name in typeDeclaration.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var symbol = semanticModel.GetSymbolInfo(name, ct).Symbol;
            if (symbol is not null) used.Add(symbol.OriginalDefinition);
        }

        foreach (var (symbol, token) in candidates)
        {
            if (!used.Contains(symbol.OriginalDefinition))
                context.ReportDiagnostic(Diagnostic.Create(Rule, token.GetLocation(), symbol.Name));
        }
    }

    // No modifier at all defaults to `private` for a class/struct member — just as much a candidate
    // as an explicit `private` keyword.
    private static bool IsPrivate(SyntaxTokenList modifiers) =>
        !modifiers.Any(SyntaxKind.PublicKeyword)
        && !modifiers.Any(SyntaxKind.ProtectedKeyword)
        && !modifiers.Any(SyntaxKind.InternalKeyword);
}
