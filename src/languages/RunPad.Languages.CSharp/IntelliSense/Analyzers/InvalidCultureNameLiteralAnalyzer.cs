using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести пятьдесят пятый проход, batch) — own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as InvalidVersionLiteralFormatAnalyzer (RPAD0247, проход 254), but
// verified differently: rather than approximating culture-name validity with a pattern (there's no
// simple format rule — valid names range from "en" to "zh-Hans-CN" to obscure regional variants), this
// analyzer calls the REAL `CultureInfo.GetCultureInfo` in-process on the literal and reports only if
// THAT call itself throws `CultureNotFoundException` — the exact same check the user's own code would
// perform at runtime, so there's no approximation or false-positive risk at all. `Regex`'s pattern
// argument was considered for the same "run the real validator" treatment in this проход's PROBE round
// and dropped — a built-in analyzer already reports "Regex issue: ..." for a syntactically invalid
// pattern literal, so there was nothing left to add there.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCultureNameLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0248";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Culture name literal is not a recognized culture",
        messageFormat: "'{0}' always throws CultureNotFoundException at runtime — '{1}' is not a recognized culture name",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "CultureInfo.GetCultureInfo/the CultureInfo(string) constructor throw CultureNotFoundException for a name that isn't a recognized culture.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        ArgumentListSyntax? argumentList;
        string callText;
        ISymbol? symbol;
        var ct = context.CancellationToken;

        switch (context.Node)
        {
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetCultureInfo" } } invocation:
                argumentList = invocation.ArgumentList;
                callText = "CultureInfo.GetCultureInfo(...)";
                symbol = context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol;
                break;
            case BaseObjectCreationExpressionSyntax creation:
                argumentList = creation.ArgumentList;
                callText = "new CultureInfo(...)";
                symbol = context.SemanticModel.GetSymbolInfo(creation, ct).Symbol;
                break;
            default:
                return;
        }

        if (argumentList is not { Arguments: [{ Expression: var nameArg }, ..] }) return;
        if (context.SemanticModel.GetConstantValue(nameArg, ct).Value is not string name) return;

        if (symbol is not IMethodSymbol method) return;
        var isMatch = method switch
        {
            { MethodKind: MethodKind.Ordinary, Name: "GetCultureInfo" } => method.ContainingType?.ToDisplayString() == "System.Globalization.CultureInfo",
            { MethodKind: MethodKind.Constructor } => method.ContainingType?.ToDisplayString() == "System.Globalization.CultureInfo",
            _ => false,
        };
        if (!isMatch) return;

        try
        {
            CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), callText, name));
        }
    }
}
