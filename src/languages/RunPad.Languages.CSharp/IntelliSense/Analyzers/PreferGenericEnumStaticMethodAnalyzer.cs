using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят пятый проход, batch) — same "prefer the generic overload" theme as
// EnumGetValuesCastAnalyzer (RPAD0067, проход 160). `Enum.Parse(typeof(T), s)`/`Enum.IsDefined(typeof(T),
// v)` are the pre-generic-overload idiom — both go through the non-generic Type/object-based API and
// box/cast the result. `Enum.Parse<T>(s)`/`Enum.IsDefined<T>(v)` (both .NET 5+, always available given
// this project's own MinSupportedSdkMajorVersion=6) do the same thing strongly-typed, without the
// boxing round-trip.
// Extended (сто шестьдесят седьмой проход, batch) to also cover `Enum.GetName(typeof(T), v)` →
// `Enum.GetName<T>(v)` — same idiom, same fix, one more method in the same family.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferGenericEnumStaticMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0090";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use the generic Enum.Parse/IsDefined overload",
        messageFormat: "Use 'Enum.{0}<T>(...)' instead of 'Enum.{0}(typeof(T), ...)' — avoids the boxing round-trip through Type/object",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Enum.Parse(Type, ...)/Enum.IsDefined(Type, ...) box through object; the generic overloads are strongly-typed and avoid it.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetMethodNames = ImmutableHashSet.Create("Parse", "IsDefined", "GetName");

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
        if (!TargetMethodNames.Contains(methodName)) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: TypeOfExpressionSyntax typeOfExpression }, ..])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return;
        if (method.IsGenericMethod || method.ContainingType?.ToDisplayString() != "System.Enum") return;

        var enumType = context.SemanticModel.GetTypeInfo(typeOfExpression.Type, ct).Type;
        if (enumType is not { TypeKind: TypeKind.Enum }) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }
}
