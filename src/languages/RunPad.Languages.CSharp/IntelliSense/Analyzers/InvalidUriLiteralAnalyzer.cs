using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "run the
// real validator" theme as InvalidIPAddressLiteralAnalyzer/InvalidMailAddressLiteralAnalyzer (RPAD0249,
// RPAD0250): `new Uri(s)`/`new Uri(s, kind)` throws UriFormatException for a string
// that doesn't parse as a URI of the requested UriKind — rather than hand-writing URI-syntax validation,
// this analyzer calls the REAL `Uri.TryCreate` in-process on the literal (with the same UriKind the
// constructor call itself would use) and reports only if THAT call returns false — empirically confirmed
// to agree with the constructor's throw/no-throw outcome across a wide range of inputs and all three
// UriKind values before writing this. URI syntax validation (as opposed to DNS resolution, which Uri
// never performs) is a pure string algorithm with no external, environment-dependent data table behind
// it, so there's no risk of the analysis machine and the deployment target disagreeing on what's valid.
// The 2-arg `UriKind` value is only used when it's itself a compile-time constant (`UriKind.Absolute`
// etc.) — the legacy `(string, bool dontEscape)` overload is naturally excluded since its second
// parameter isn't a `UriKind`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidUriLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0255";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Uri constructor argument is not a valid URI",
        messageFormat: "'new Uri(...)' always throws UriFormatException at runtime — '{0}' is not a valid {1} URI",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The Uri constructor throws UriFormatException when the input string doesn't parse as a URI of the requested UriKind.");

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
        if (creation.ArgumentList is not { Arguments: [{ Expression: var uriStringArg }, ..] arguments }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(uriStringArg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Uri") return;

        UriKind kind;
        if (arguments.Count == 1)
        {
            if (ctor.Parameters is not [{ Type.SpecialType: SpecialType.System_String }]) return;
            kind = UriKind.RelativeOrAbsolute;
        }
        else
        {
            if (ctor.Parameters is not [_, { Type.Name: "UriKind" }, ..]) return;
            if (context.SemanticModel.GetConstantValue(arguments[1].Expression, ct).Value is not int kindValue) return;
            kind = (UriKind)kindValue;
        }

        if (Uri.TryCreate(value, kind, out _)) return;

        var kindDescription = kind switch
        {
            UriKind.Absolute => "absolute",
            UriKind.Relative => "relative",
            _ => "relative or absolute",
        };
        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), value, kindDescription));
    }
}
