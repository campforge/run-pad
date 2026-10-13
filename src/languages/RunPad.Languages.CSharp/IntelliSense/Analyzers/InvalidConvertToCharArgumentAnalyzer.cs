using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. A fresh
// mechanism within the "literal argument that always throws" theme: `Convert.ToChar(string value)`
// throws FormatException at runtime unless `value` is EXACTLY one character long — an empty string or a
// multi-character string both throw — confirmed empirically via scratch `dotnet run` script, not
// assumed. Scoped to a LITERAL string argument only, same reasoning as the rest of this family.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidConvertToCharArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0310";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Convert.ToChar(string) argument is not exactly one character",
        messageFormat: "'Convert.ToChar(\"{0}\")' always throws FormatException at runtime — the string must be exactly one character long",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Convert.ToChar(string value) throws FormatException when value is not exactly one character long.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToChar" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var valueArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(valueArg, ct).Value is not string value) return;
        if (value.Length == 1) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { IsStatic: true, Name: "ToChar" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Convert") return;
        if (method.Parameters is not [{ Type.SpecialType: SpecialType.System_String }]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
    }
}
