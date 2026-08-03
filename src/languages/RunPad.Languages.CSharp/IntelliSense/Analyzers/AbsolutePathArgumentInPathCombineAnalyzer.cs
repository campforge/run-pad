using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести сорок третий проход, batch) — own pick, not a Roslynator port. A correctness
// finding: `Path.Combine` is documented to discard every earlier segment as soon as it reaches one that
// is itself a rooted (absolute) path — `Path.Combine(baseDir, "/etc/passwd")` returns just
// "/etc/passwd", silently dropping `baseDir` entirely. This is a well-known, easy-to-miss surprise
// (usually happens when a value that's SUPPOSED to be relative turns out to have a leading slash, e.g.
// from user input or a URL-shaped path segment). Scoped to a LITERAL string argument after the first
// position — proving an arbitrary expression is always rooted would need real data-flow analysis;
// checked against BOTH Windows (drive letter, leading `\`) and Unix (leading `/`) rooted-path shapes
// since the target OS isn't known statically.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AbsolutePathArgumentInPathCombineAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0239";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Path.Combine argument looks like an absolute path",
        messageFormat: "'{0}' looks like a rooted path — Path.Combine discards every earlier segment as soon as it reaches a rooted one",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Path.Combine returns just the last rooted (absolute) segment it sees, silently discarding everything passed before it.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Combine" })
            return;
        if (invocation.ArgumentList.Arguments.Count < 2) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Combine" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.IO.Path") return;

        var arguments = invocation.ArgumentList.Arguments;
        for (var i = 1; i < arguments.Count; i++)
        {
            if (context.SemanticModel.GetConstantValue(arguments[i].Expression, ct).Value is not string value)
                continue;
            if (!LooksRooted(value)) continue;

            context.ReportDiagnostic(Diagnostic.Create(Rule, arguments[i].GetLocation(), value));
            return;
        }
    }

    private static bool LooksRooted(string path)
    {
        if (path.Length == 0) return false;
        if (path[0] is '/' or '\\') return true;
        return path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
    }
}
