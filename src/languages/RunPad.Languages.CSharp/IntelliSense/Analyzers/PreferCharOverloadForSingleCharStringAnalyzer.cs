using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят восьмой проход, batch) — own pick, not a Roslynator port.
// `s.LastIndexOf("x")`/`s.Contains("x")`/`s.Replace("x", "y")` with a single-character string literal
// argument go through the general substring-search machinery for something a char comparison answers
// directly and faster. Deliberately excludes StartsWith/EndsWith/IndexOf — those single-string-argument
// overloads are ALSO flagged by CultureSensitiveStringMethodAnalyzer (RPAD0031) for a different reason
// (they default to CurrentCulture comparison), and double-messaging the same call site with two
// separate diagnostics would be noise; Contains/LastIndexOf/Replace's string overloads already use
// ordinal comparison, so there's no such overlap for them.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferCharOverloadForSingleCharStringAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0109";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer the char overload for a single-character string argument",
        messageFormat: "Use the char overload of '{0}' instead of a single-character string literal — avoids the general substring-search machinery",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Passing a single-character string literal to LastIndexOf/Contains/Replace goes through substring search for something the char overload answers directly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> SingleArgMethodNames = ImmutableHashSet.Create("LastIndexOf", "Contains");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            return;

        var ct = context.CancellationToken;

        if (methodName == "Replace")
        {
            if (invocation.ArgumentList.Arguments is not [{ Expression: var replaceArg1 }, { Expression: var replaceArg2 }])
                return;
            if (!IsSingleCharStringLiteral(replaceArg1) || !IsSingleCharStringLiteral(replaceArg2)) return;
            if (!IsStringMethod(context, invocation, methodName, 2, ct)) return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
            return;
        }

        if (!SingleArgMethodNames.Contains(methodName)) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var soleArg }]) return;
        if (!IsSingleCharStringLiteral(soleArg)) return;
        if (!IsStringMethod(context, invocation, methodName, 1, ct)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }

    private static bool IsSingleCharStringLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
        && literal.Token.ValueText.Length == 1;

    private static bool IsStringMethod(
        SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, string methodName, int expectedParamCount, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        if (method.Name != methodName || method.Parameters.Length != expectedParamCount) return false;
        return method.ContainingType?.SpecialType == SpecialType.System_String;
    }
}
