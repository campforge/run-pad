using System.Collections.Immutable;
using System.Net;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #1 from the forty-first research subagent survey,
// re-verified independently before implementing. `System.Net.Cookie.Port` setter (string) — a separate
// property from the already-covered `Cookie` constructor `name` argument (`InvalidCookieNameLiteralAnalyzer`,
// RPAD0379, which never touches `Port`). Confirmed empirically: the RFC2965 port-list value MUST be
// wrapped in double quotes (e.g. `"80"` or `"80,443"`, i.e. the literal C# string needs to CONTAIN quote
// characters) — a bare unquoted `80` always throws `System.Net.CookieException` ("The 'Port'='{value}'
// part of the cookie is invalid."), as does a quoted-but-non-numeric value like `"abc"`. Confirmed
// construction-independent (trap #8 checked): identical behavior probed against both a 2-arg
// `new Cookie(name, value)` and a 4-arg `new Cookie(name, value, path, domain)` — the check depends only
// on the `Port` literal itself, not on how the `Cookie` receiver was constructed. No
// `[SupportedOSPlatform]` gating, no side effects — pure string-grammar validation.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCookiePortLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0522";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Cookie.Port literal is not a valid port list",
        messageFormat: "'Cookie.Port = ...' always throws CookieException at runtime — '{0}' is not a valid port list (must be double-quoted, for example \"80\" or \"80,443\")",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "System.Net.Cookie.Port throws CookieException unless the value is a double-quoted, comma-separated list of numeric ports.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Port" } access)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: "Port" })
            return;
        if (context.SemanticModel.GetConstantValue(assignment.Right, ct).Value is not string port) return;

        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Net.Cookie") return;

        try
        {
            _ = new Cookie("n", "v") { Port = port };
        }
        catch (CookieException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), port));
        }
    }
}
