using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто пятьдесят четвёртый проход, batch) — a `static readonly` field whose initializer
// is already a genuine compile-time constant (verified via SemanticModel.GetConstantValue — not a
// guess) could just be `const` instead: no per-type static-constructor initialization needed, and the
// value gets inlined at every call site. Only offered for the types C# actually allows as const
// (built-in primitives, string, enum) — reference types (other than string) can never be const,
// regardless of how their value is expressed.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferConstOverStaticReadonlyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0052";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer const over static readonly",
        messageFormat: "'{0}' is initialized to a compile-time constant — it could be 'const' instead of 'static readonly'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A static readonly field whose value is a genuine compile-time constant could be const instead, avoiding static-constructor initialization and letting the value be inlined at call sites.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.FieldDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var field = (FieldDeclarationSyntax)context.Node;
        if (!field.Modifiers.Any(SyntaxKind.StaticKeyword) || !field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
            return;

        var ct = context.CancellationToken;
        var type = context.SemanticModel.GetTypeInfo(field.Declaration.Type, ct).Type;
        if (!IsConstEligibleType(type)) return;

        foreach (var variable in field.Declaration.Variables)
        {
            if (variable.Initializer is not { Value: { } initializerExpr }) continue;
            if (!context.SemanticModel.GetConstantValue(initializerExpr, ct).HasValue) continue;

            context.ReportDiagnostic(Diagnostic.Create(Rule, variable.Identifier.GetLocation(), variable.Identifier.Text));
        }
    }

    private static bool IsConstEligibleType(ITypeSymbol? type) => type switch
    {
        {
            SpecialType: SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_String
            or SpecialType.System_SByte or SpecialType.System_Byte
            or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal,
        } => true,
        { TypeKind: TypeKind.Enum } => true,
        _ => false,
    };
}
