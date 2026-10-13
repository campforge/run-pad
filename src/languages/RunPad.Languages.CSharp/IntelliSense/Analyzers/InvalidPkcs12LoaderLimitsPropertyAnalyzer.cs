using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #2 from the thirty-second research subagent survey,
// re-verified independently before implementing. First touch of `System.Security.Cryptography.
// X509Certificates.Pkcs12LoaderLimits`. Five `int?` properties (`MacIterationLimit`,
// `IndividualKdfIterationLimit`, `TotalKdfIterationLimit`, `MaxKeys`, `MaxCertificates`), bundled under
// one diagnostic (precedent RPAD0426). Confirmed empirically: a negative `int` literal assigned to any
// of the five always throws `ArgumentOutOfRangeException` immediately and synchronously from the
// property setter itself (not lazily on later use); `0`, `int.MaxValue`, and assigning `null` (clears
// the limit) are all valid. No `[SupportedOSPlatform]` gating, no side effects — a plain in-memory
// options object, no I/O. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidPkcs12LoaderLimitsPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0509";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Pkcs12LoaderLimits property assignment is negative",
        messageFormat: "'Pkcs12LoaderLimits.{0} = {1}' always throws ArgumentOutOfRangeException at runtime — the value must be non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Pkcs12LoaderLimits.MacIterationLimit/IndividualKdfIterationLimit/TotalKdfIterationLimit/MaxKeys/MaxCertificates throw ArgumentOutOfRangeException when assigned a negative value.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> PropertyNames = ImmutableHashSet.Create(
        "MacIterationLimit", "IndividualKdfIterationLimit", "TotalKdfIterationLimit", "MaxKeys", "MaxCertificates");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: var propertyName } access)
            return;
        if (!PropertyNames.Contains(propertyName)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(assignment.Right, ct).Value is not int value) return;
        if (value >= 0) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var symbolName })
            return;
        if (symbolName != propertyName) return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Security.Cryptography.X509Certificates.Pkcs12LoaderLimits") return;

        var limits = new Pkcs12LoaderLimits();
        try
        {
            switch (propertyName)
            {
                case "MacIterationLimit": limits.MacIterationLimit = value; break;
                case "IndividualKdfIterationLimit": limits.IndividualKdfIterationLimit = value; break;
                case "TotalKdfIterationLimit": limits.TotalKdfIterationLimit = value; break;
                case "MaxKeys": limits.MaxKeys = value; break;
                default: limits.MaxCertificates = value; break;
            }
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), propertyName, value));
        }
    }
}
