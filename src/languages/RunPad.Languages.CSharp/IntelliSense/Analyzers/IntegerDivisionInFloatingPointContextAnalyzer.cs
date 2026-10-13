using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// A classic, extremely common C#/Java-family bug
// (own pick, not a Roslynator port — same "real bug" bar as SelfReferencingPropertyAnalyzer/
// CompareNaNCorrectlyAnalyzer): `double result = a / b;` where a and b are both integers performs
// INTEGER division first (truncating any fractional part), and only THEN converts the truncated
// result to double — not the fractional division the floating-point target type suggests was
// intended. Deliberately narrow: only the two most common shapes (a variable declared with an
// explicit floating-point type, or an assignment to an already floating-point-typed target) — an
// implicit `var` declaration is correctly never flagged, since its inferred type would itself be
// the (correct, non-buggy) integer result.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IntegerDivisionInFloatingPointContextAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0047";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Integer division in a floating-point context",
        messageFormat: "This division uses integer operands — the fractional part is truncated BEFORE the result converts to a floating-point type",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Dividing two integers truncates the result before it's ever converted to float/double/decimal — cast at least one operand first if fractional division was intended.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeVariableDeclarator, SyntaxKind.VariableDeclarator);
        context.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void AnalyzeVariableDeclarator(SyntaxNodeAnalysisContext context)
    {
        var declarator = (VariableDeclaratorSyntax)context.Node;
        if (declarator.Initializer?.Value is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.DivideExpression } division)
            return;
        if (declarator.Parent is not VariableDeclarationSyntax { Type: { } typeSyntax }) return;

        var declaredType = context.SemanticModel.GetTypeInfo(typeSyntax, context.CancellationToken).Type;
        if (IsFloatingPoint(declaredType)) CheckDivisionOperands(context, division);
    }

    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Right is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.DivideExpression } division) return;

        var leftType = context.SemanticModel.GetTypeInfo(assignment.Left, context.CancellationToken).Type;
        if (IsFloatingPoint(leftType)) CheckDivisionOperands(context, division);
    }

    private static void CheckDivisionOperands(SyntaxNodeAnalysisContext context, BinaryExpressionSyntax division)
    {
        var ct = context.CancellationToken;
        var leftType = context.SemanticModel.GetTypeInfo(division.Left, ct).Type;
        var rightType = context.SemanticModel.GetTypeInfo(division.Right, ct).Type;
        if (IsIntegerType(leftType) && IsIntegerType(rightType))
            context.ReportDiagnostic(Diagnostic.Create(Rule, division.GetLocation()));
    }

    private static bool IsFloatingPoint(ITypeSymbol? type) =>
        type?.SpecialType is SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;

    private static bool IsIntegerType(ITypeSymbol? type) => type?.SpecialType is
        SpecialType.System_SByte or SpecialType.System_Byte
        or SpecialType.System_Int16 or SpecialType.System_UInt16
        or SpecialType.System_Int32 or SpecialType.System_UInt32
        or SpecialType.System_Int64 or SpecialType.System_UInt64;
}
