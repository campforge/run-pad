using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Found by the third research subagent (candidate #1 of its
// ranked list), then independently re-verified before implementation per this session's standing
// discipline. InvalidGuidLiteralFormatAnalyzer (RPAD0246, проход 253) only registers on
// InvocationExpression and matches the static "Parse" call — it never handles ObjectCreationExpression,
// so `new Guid("...")` was a genuine, unguarded gap. Rather than reuse RPAD0246's hand-rolled
// ValidGuidPattern regex (which deliberately excludes the "X" hex-array format), this analyzer uses the
// "run the real validator" technique — calling the actual `Guid(string)` constructor in-process on the
// literal — which naturally and correctly accepts every format the real constructor accepts (D/N/B/P/X,
// plus leading/trailing whitespace) with no risk of the regex/reality drift RPAD0246 has to live with.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidGuidConstructorLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0402";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Guid constructor argument is not a recognized Guid format",
        messageFormat: "'new Guid(...)' always throws FormatException at runtime — '{0}' doesn't match any of Guid's recognized formats (32 hex digits, optionally grouped 8-4-4-4-12 with dashes, optionally wrapped in braces or parentheses, or the hex-array \"X\" format)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The Guid(string) constructor throws FormatException when the argument doesn't match any of Guid's recognized textual formats.");

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
        var ct = context.CancellationToken;

        if (creation.ArgumentList is not { Arguments: [{ Expression: var arg }] }) return;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string text) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                ContainingType.SpecialType: SpecialType.None,
            } method)
        {
            return;
        }

        if (method.ContainingType.ToDisplayString() != "System.Guid") return;
        if (method.Parameters is not [{ Type.SpecialType: SpecialType.System_String }]) return;

        try
        {
            _ = new Guid(text);
        }
        catch (FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), text));
        }
    }
}
