using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. A correctness
// finding: `Enum.IsDefined` only checks for an EXACT match against one of the enum's named members — for
// a `[Flags]` enum, a value that's a legitimate COMBINATION of flags (e.g. `Read | Write`, where only
// `Read` and `Write` are individually named, not the combination) is not itself a named member, so
// `IsDefined` incorrectly returns `false` for it even though it's a perfectly valid flags value. Fires
// on both `Enum.IsDefined(typeof(T), value)` and the generic `Enum.IsDefined<T>(value)` (RPAD0090) form
// — the typeof form can ALSO trigger PreferGenericEnumStaticMethodAnalyzer (RPAD0090) on the same call,
// for the unrelated reason of avoiding the boxing round-trip; that's a different, independently valid
// concern, not a duplicate of this one, so both are allowed to fire together.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IsDefinedOnFlagsEnumAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0244";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Enum.IsDefined on a [Flags] enum misses valid combined values",
        messageFormat: "'Enum.IsDefined' only matches a single named member — for a [Flags] enum like '{0}', a legitimate combination of flags such as 'A | B' is not itself named, so this incorrectly returns false for it",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Enum.IsDefined checks for an exact match against a single named member, which is the wrong check for [Flags] enums — a valid combination of flags isn't itself a named member.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "IsDefined" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "IsDefined" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Enum") return;

        ITypeSymbol? enumType = method.IsGenericMethod && method.TypeArguments is [var typeArgument]
            ? typeArgument
            : invocation.ArgumentList.Arguments is [{ Expression: TypeOfExpressionSyntax typeOfExpr }, ..]
                ? context.SemanticModel.GetTypeInfo(typeOfExpr.Type, ct).Type
                : null;

        if (enumType is not { TypeKind: TypeKind.Enum }) return;
        if (!HasFlagsAttribute(enumType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), enumType.Name));
    }

    private static bool HasFlagsAttribute(ITypeSymbol enumType)
    {
        foreach (var attribute in enumType.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == "System.FlagsAttribute") return true;
        }
        return false;
    }
}
