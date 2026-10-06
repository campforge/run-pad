using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести шестьдесят седьмой проход, batch) — own pick, not a Roslynator port. A correctness
// finding: `new SomeDisposableType(...);` as a bare statement — the whole statement is just the object
// creation, nothing chained off it and nothing capturing the result — constructs the object and then
// immediately discards the only reference to it, so `Dispose()`/`DisposeAsync()` can never be called on
// it. This is different from `new Foo(...).Bar();` (a chained call, not a bare creation) and from `using
// var x = new Foo(...);`/`var x = new Foo(...); x.Dispose();` (both keep a reference) — neither of those
// syntactic shapes matches an `ExpressionStatementSyntax` whose `Expression` is directly the
// `ObjectCreationExpressionSyntax`/`ImplicitObjectCreationExpressionSyntax`, so this analyzer can't
// misfire on them. Confirmed via PROBE that nothing else in this project or the IDE's own diagnostics
// already flags this. Covers both `IDisposable` and `IAsyncDisposable`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DiscardedDisposableObjectCreationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0260";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Disposable object created and immediately discarded",
        messageFormat: "'{0}' implements {1} but the newly-created instance is discarded without being assigned or wrapped in 'using' — Dispose() can never be called on it",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A bare `new SomeDisposableType(...);` statement constructs the object and immediately discards the only reference to it, so its Dispose()/DisposeAsync() can never run.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ExpressionStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var statement = (ExpressionStatementSyntax)context.Node;
        if (statement.Expression is not (ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax))
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetTypeInfo(statement.Expression, ct).Type is not INamedTypeSymbol createdType)
            return;

        var disposableType = context.Compilation.GetTypeByMetadataName("System.IDisposable");
        var asyncDisposableType = context.Compilation.GetTypeByMetadataName("System.IAsyncDisposable");

        var interfaceName =
            IsOrImplements(createdType, disposableType) ? "IDisposable" :
            IsOrImplements(createdType, asyncDisposableType) ? "IAsyncDisposable" :
            null;
        if (interfaceName is null) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, statement.GetLocation(), createdType.Name, interfaceName));
    }

    private static bool IsOrImplements(INamedTypeSymbol type, INamedTypeSymbol? targetInterface)
    {
        if (targetInterface is null) return false;
        return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, targetInterface));
    }
}
