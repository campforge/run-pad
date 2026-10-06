using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двухсотшестидесятый проход, batch) — own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as InvalidGuidLiteralFormatAnalyzer (RPAD0246, проход 253), but for
// `Enum.Parse<TEnum>(s)`/`Enum.Parse(typeof(TEnum), s)`: it throws ArgumentException when `s` (split on
// commas, each part trimmed) contains a part that is neither a defined member name of TEnum nor a
// numeric literal — Enum.Parse accepts arbitrary numeric strings even for undefined underlying values
// (like a cast would), and combines multiple comma-separated parts with bitwise OR regardless of whether
// TEnum actually has [Flags]. TEnum's member names are known at COMPILE time via the semantic model
// (unlike a culture name or IP address, there's no runtime/environment-dependent data table involved),
// so rather than calling a real parser this analyzer enumerates TEnum's members directly. Deliberately
// conservative about case: `Enum.Parse`'s 2-arg overloads are case-sensitive, but the 3-arg overloads
// take a runtime `ignoreCase` bool that isn't always a literal — rather than trying to resolve it, this
// analyzer only reports when a part matches NO member name in EITHER casing, which always throws
// regardless of what `ignoreCase` turns out to be at runtime.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidEnumParseLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0254";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Enum.Parse argument does not match a defined enum member",
        messageFormat: "'Enum.Parse' always throws ArgumentException at runtime — '{0}' is not a defined member of '{1}' and is not a numeric literal",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Enum.Parse throws ArgumentException when a comma-separated part of the input is neither a defined member name of the enum type nor a numeric literal.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly Regex NumericPattern = new(@"^[+-]?\d+$", RegexOptions.Compiled);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Parse" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Enum") return;

        var arguments = invocation.ArgumentList.Arguments;
        INamedTypeSymbol? enumType;
        ExpressionSyntax literalArg;

        if (method.IsGenericMethod && method.TypeArguments is [INamedTypeSymbol genericEnumType])
        {
            if (arguments is not [{ Expression: var genericLiteralArg }, ..]) return;
            enumType = genericEnumType;
            literalArg = genericLiteralArg;
        }
        else
        {
            if (arguments is not [{ Expression: TypeOfExpressionSyntax typeOfExpr }, { Expression: var nonGenericLiteralArg }, ..])
                return;
            enumType = context.SemanticModel.GetTypeInfo(typeOfExpr.Type, ct).Type as INamedTypeSymbol;
            literalArg = nonGenericLiteralArg;
        }

        if (enumType is not { TypeKind: TypeKind.Enum }) return;
        if (context.SemanticModel.GetConstantValue(literalArg, ct).Value is not string value) return;

        var memberNames = enumType.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst).Select(f => f.Name).ToArray();

        foreach (var rawPart in value.Split(','))
        {
            var part = rawPart.Trim();
            if (NumericPattern.IsMatch(part)) continue;
            if (memberNames.Any(n => n.Equals(part, StringComparison.OrdinalIgnoreCase))) continue;

            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), part, enumType.Name));
            return;
        }
    }
}
