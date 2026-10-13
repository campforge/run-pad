using System.Collections.Immutable;
using System.Net.Mail;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "run
// the real validator" theme as InvalidCultureNameLiteralAnalyzer (RPAD0248)/InvalidIPAddressLiteralAnalyzer
// (RPAD0249, both проход 255-256): `new MailAddress(s)` throws `FormatException` for a string that
// isn't a syntactically valid email address — email address syntax is notoriously easy to get subtly
// wrong with a hand-written regex (quoted local parts, comments, IP-literal domains), so rather than
// approximate it, this analyzer constructs the REAL `System.Net.Mail.MailAddress` in-process on the
// literal and reports only if THAT construction itself throws — the exact same check the user's own
// code would perform at runtime. Email address SYNTAX validation (unlike DNS resolution, which
// MailAddress does not perform) has no external, environment-dependent data behind it, so there's no
// risk of the analysis machine and the deployment target disagreeing.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidMailAddressLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0250";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "MailAddress argument is not a syntactically valid email address",
        messageFormat: "'new MailAddress(...)' always throws FormatException at runtime — '{0}' is not a syntactically valid email address",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The MailAddress(string) constructor throws FormatException for a string that isn't a syntactically valid email address.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var arg }, ..] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Net.Mail.MailAddress") return;

        if (IsValidMailAddress(value)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), value));
    }

    private static bool IsValidMailAddress(string value)
    {
        try
        {
            _ = new MailAddress(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
