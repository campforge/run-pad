using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Threading.Channels.BoundedChannelOptions(int
// capacity)` constructor and its `FullMode` property setter. Confirmed empirically: the constructor
// throws `ArgumentOutOfRangeException` for a negative `capacity` (note: `0` is itself VALID — a
// zero-capacity rendezvous channel — confirmed rather than assumed, since a naive guess might expect 0 to
// also be rejected like several other "must be positive" candidates in this catalog); `FullMode` throws
// `ArgumentOutOfRangeException` eagerly at property-set time for any value outside 0-3, with no need to
// ever call `Channel.CreateBounded`. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. No side
// effects — `BoundedChannelOptions` is a pure managed configuration object; constructing it and setting
// its properties never creates an actual channel.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBoundedChannelOptionsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0456";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "BoundedChannelOptions argument or property is invalid",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new BoundedChannelOptions(capacity) throws ArgumentOutOfRangeException for a negative capacity, and BoundedChannelOptions.FullMode throws for a value outside its own enum's defined range.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeConstructor, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeFullMode, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void AnalyzeConstructor(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not BaseObjectCreationExpressionSyntax creation) return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var capacityArg }] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(capacityArg, ct).Value is not int capacity) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                Parameters: [{ Type.SpecialType: SpecialType.System_Int32 }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Threading.Channels.BoundedChannelOptions") return;

        try
        {
            _ = new BoundedChannelOptions(capacity);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), "new BoundedChannelOptions(...)", "capacity must not be negative"));
        }
    }

    private static void AnalyzeFullMode(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "FullMode" } access)
            return;
        if (assignment.Right is not CastExpressionSyntax { Expression: var innerExpr }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(innerExpr, ct).Value is not int rawValue) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: "FullMode" })
            return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Threading.Channels.BoundedChannelOptions") return;

        var enumTypeName = context.SemanticModel.GetTypeInfo(assignment.Right, ct).Type?.ToDisplayString();
        if (enumTypeName != "System.Threading.Channels.BoundedChannelFullMode") return;

        try
        {
            new BoundedChannelOptions(1) { FullMode = (BoundedChannelFullMode)rawValue };
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), "BoundedChannelOptions.FullMode = ...", "value is outside the valid range for BoundedChannelFullMode"));
        }
    }
}
