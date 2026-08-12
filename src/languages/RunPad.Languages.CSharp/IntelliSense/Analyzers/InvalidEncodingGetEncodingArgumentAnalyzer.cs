using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста шестьдесят второй проход, batch) — own pick, not a Roslynator port. Two related
// overloads of `Encoding.GetEncoding`, each with its own failure mode: `GetEncoding(int codepage)`
// throws ArgumentOutOfRangeException for a literal outside the documented `[0, 65535]` range —
// confirmed empirically via scratch `dotnet run` script (a simple, literal-derivable boundary, no need
// for the real-validator technique) — and `GetEncoding(string name)` throws ArgumentException for a
// name that isn't a registered encoding — confirmed empirically via the "run the real validator"
// technique (same reasoning as `InvalidCultureNameLiteralAnalyzer`/RPAD0247 for the analogous
// `CultureInfo` name-validity case): looking up a registered encoding by name is a pure, deterministic,
// side-effect-free operation, so calling the REAL `Encoding.GetEncoding(name)` on the literal and
// checking whether it throws is safer than trying to enumerate every valid encoding name by hand.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidEncodingGetEncodingArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0355";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Encoding.GetEncoding argument is invalid",
        messageFormat: "'Encoding.GetEncoding({0})' always throws {1} at runtime — {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Encoding.GetEncoding(int codepage) throws ArgumentOutOfRangeException when codepage is outside [0, 65535], and Encoding.GetEncoding(string name) throws ArgumentException when name isn't a registered encoding.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetEncoding" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        var constant = context.SemanticModel.GetConstantValue(arg, ct);
        if (!constant.HasValue) return;

        string argDisplay;
        string exceptionType;
        string reason;
        switch (constant.Value)
        {
            case int codepage when codepage is < 0 or > 65535:
                argDisplay = codepage.ToString();
                exceptionType = "ArgumentOutOfRangeException";
                reason = "codepage must be between 0 and 65535";
                break;
            case string name when !IsRegisteredEncodingName(name):
                argDisplay = $"\"{name}\"";
                exceptionType = "ArgumentException";
                reason = $"'{name}' is not a registered encoding name";
                break;
            default:
                return;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "GetEncoding" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Text.Encoding") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), argDisplay, exceptionType, reason));
    }

    private static bool IsRegisteredEncodingName(string name)
    {
        try
        {
            _ = Encoding.GetEncoding(name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
