using System.Collections.Immutable;
using System.Net;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста восемьдесят седьмой проход, batch) — own pick, not a Roslynator port; the research
// subagent's ranked list (проход 377) is now fully exhausted (проходы 377-386, all 8 candidates shipped) —
// found by direct brainstorming afterward, scanning for un-mined BCL namespaces against the existing
// analyzer catalog (grep-confirmed no prior coverage of System.Net.Cookie before implementing). Same "run
// the real validator" theme, applied to `new System.Net.Cookie(name, value[, path[, domain]])`. Confirmed
// empirically that all three non-parameterless constructor overloads (2/3/4-arg) — and even the bare
// `Name` property setter directly — share IDENTICAL name-validation behavior (all funnel through the same
// internal check), so the analyzer always probes via the simplest 2-arg constructor regardless of which
// overload the user's call site actually uses. Confirmed empirically this is a single-exception-type,
// NAME-only situation: illegal characters in `name` (`=`, `,`, `;`, a leading `$` reserved-name prefix, or
// an empty string) throw `System.Net.CookieException`, but `value` is NOT validated by this constructor at
// all — a value containing spaces, quotes, or tabs is accepted without throwing — so this analyzer only
// ever inspects the name argument, never the value argument.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCookieNameLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0379";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Cookie name literal is not a valid cookie name",
        messageFormat: "'new Cookie(...)' always throws CookieException at runtime — '{0}' is not a valid cookie name",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The System.Net.Cookie constructor throws CookieException for a name containing '=', ',', ';', a leading '$', or an empty name.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var ct = context.CancellationToken;
        if (context.Node is not BaseObjectCreationExpressionSyntax creation) return;
        if (creation.ArgumentList is not { Arguments.Count: 2 or 3 or 4 } argumentList) return;
        if (context.SemanticModel.GetConstantValue(argumentList.Arguments[0].Expression, ct).Value is not string name) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Net.Cookie") return;

        try
        {
            _ = new Cookie(name, "value");
        }
        catch (CookieException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), name));
        }
    }
}
