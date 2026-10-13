using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #1 from the twenty-eighth research subagent survey,
// re-verified independently before implementing. `System.Security.Cryptography.X509Certificates.
// X509ChainPolicy` — an eighth productive revisit of `X509Certificates` (after RPAD0472/0473/0477/
// 0483/0485/0488). Four enum property setters, bundled under one diagnostic (precedent RPAD0426):
// `RevocationMode` (`X509RevocationMode`, valid `0..2`), `RevocationFlag` (`X509RevocationFlag`,
// valid `0..2`), `TrustMode` (`X509ChainTrustMode`, valid `0..1`), `VerificationFlags`
// (`[Flags] X509VerificationFlags`, valid mask `0..4095`) — all confirmed empirically to throw
// `ArgumentException` ("Illegal enum value: value.") for an out-of-range value, and confirmed all
// four enums are `int`-backed via `Enum.GetUnderlyingType` (not assumed). No `[SupportedOSPlatform]`
// gating, no side effects — pure in-memory settings object, no OS/chain-building calls performed.
// Culture-irrelevant (no formatting).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidX509ChainPolicyPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0496";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "X509ChainPolicy property assignment is invalid",
        messageFormat: "'X509ChainPolicy.{0} = {1}' always throws ArgumentException at runtime — the value is not a defined {2} member",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "X509ChainPolicy.RevocationMode/RevocationFlag/TrustMode/VerificationFlags throw ArgumentException for a value outside their own defined range.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, string> EnumTypeNames = ImmutableDictionary.CreateRange(
        [
            KeyValuePair.Create("RevocationMode", "X509RevocationMode"),
            KeyValuePair.Create("RevocationFlag", "X509RevocationFlag"),
            KeyValuePair.Create("TrustMode", "X509ChainTrustMode"),
            KeyValuePair.Create("VerificationFlags", "X509VerificationFlags"),
        ]);

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
        if (!EnumTypeNames.TryGetValue(propertyName, out var enumTypeName)) return;

        var ct = context.CancellationToken;
        if (ToInt32Constant(context.SemanticModel.GetConstantValue(assignment.Right, ct)) is not { } rawValue) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var symbolName })
            return;
        if (symbolName != propertyName) return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Security.Cryptography.X509Certificates.X509ChainPolicy") return;

        var policy = new X509ChainPolicy();
        try
        {
            switch (propertyName)
            {
                case "RevocationMode": policy.RevocationMode = (X509RevocationMode)rawValue; break;
                case "RevocationFlag": policy.RevocationFlag = (X509RevocationFlag)rawValue; break;
                case "TrustMode": policy.TrustMode = (X509ChainTrustMode)rawValue; break;
                default: policy.VerificationFlags = (X509VerificationFlags)rawValue; break;
            }
        }
        catch (System.ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), propertyName, rawValue, enumTypeName));
            return;
        }
    }

    private static int? ToInt32Constant(Optional<object?> constant) => constant.Value switch
    {
        int i => i,
        byte b => b,
        sbyte sb => sb,
        short s => s,
        ushort us => us,
        uint u => unchecked((int)u),
        _ => null,
    };
}
