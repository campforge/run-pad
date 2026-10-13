using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #5 from the third research subagent survey (411-й проход,
// full tool access, empirically self-verified), re-verified independently before implementing.
// `System.Security.Cryptography.PemEncoding.Find(ReadOnlySpan<char> pemData)` — the lower-level PEM
// structure locator that the various `ImportFromPem`/`ImportFromEncryptedPem` analyzers in this catalog
// (RPAD0392/0393/0400/0401/0403/0404/0405) ultimately rely on internally, but exposed as public API in
// its own right. Confirmed empirically: single exception type, `ArgumentException`, thrown for empty
// text, garbage text, mismatched BEGIN/END labels, a missing END line, whitespace-only text, and even a
// structurally-correct BEGIN/END pair whose body isn't valid Base64 — `Find` validates the PEM envelope
// AND the base64-ness of its content in one pass (unlike `ImportFromPem`, which can distinguish
// "unrecognized structure" from "recognized structure, wrong algorithm" via two different exception
// types, `Find` doesn't parse key material at all, just the generic PEM framing, so there's only the one
// failure mode).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidPemEncodingFindLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0408";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "PemEncoding.Find argument is not a valid PEM structure",
        messageFormat: "'PemEncoding.Find(...)' always throws ArgumentException at runtime — the text does not contain a valid PEM-encoded structure",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "PemEncoding.Find(ReadOnlySpan<char>) throws ArgumentException when the text doesn't contain a recognizable PEM envelope (matching BEGIN/END labels wrapping valid Base64 content).");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Find" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string text) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Find",
                IsStatic: true,
                Parameters: [{ Type: { } paramType }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (paramType.ToDisplayString() != "System.ReadOnlySpan<char>") return;
        if (containingType.ToDisplayString() != "System.Security.Cryptography.PemEncoding") return;

        try
        {
            PemEncoding.Find(text);
        }
        catch (ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
        }
    }
}
