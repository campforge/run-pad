using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #2 from the sixth research subagent survey (full tool
// access, empirically self-verified), re-verified independently before implementing. A `new
// HashAlgorithmName(string)` literal passed as the algorithm-name argument to
// `CryptographicOperations.HashData`/`HmacData` or `IncrementalHash.CreateHash`/`CreateHMAC` — distinct
// from the already-rejected `HashAlgorithm.Create(string)`/`CryptoConfig.CreateFromName` (those return
// `null` for an unrecognized name rather than throwing; confirmed dead ends earlier this session). These
// newer `HashAlgorithmName`-based APIs genuinely throw. Confirmed empirically, matching the agent's report
// exactly: `CryptographicException` for an unrecognized name (matching is case-SENSITIVE and does not
// trim whitespace — confirmed both `"sha256"` lowercase and `" SHA256 "` padded are rejected even though
// `"SHA256"` itself is valid), `ArgumentException` for an empty string. Confirmed `new
// HashAlgorithmName(text)` alone never throws (it's a lightweight struct wrapping the string) — the
// validation happens when a CONSUMING method tries to resolve the algorithm, so this analyzer runs the
// real validator via `IncrementalHash.CreateHash` as a single canonical probe regardless of which of the
// four actual call shapes matched, since all four were confirmed to throw identically for the same bogus
// name (disposing the result on success). DELIBERATELY narrow exception catch (`CryptographicException`/
// `ArgumentException` only, not broad `Exception`) per the agent's platform-dependence warning: SHA3-256/
// 384/512 support depends on the underlying OpenSSL/OS crypto backend and could throw
// `PlatformNotSupportedException` for an otherwise-valid name on some machines — that's not "always
// throws," it's "throws on THIS machine," so it's excluded from the catch and the known-good test literals
// are restricted to the universally-available MD5/SHA1/SHA256/SHA384/SHA512.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidHashAlgorithmNameLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0426";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "HashAlgorithmName argument is not a known hash algorithm",
        messageFormat: "'{0}' always throws {1} at runtime — '{2}' is not a known hash algorithm name",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "CryptographicOperations.HashData/HmacData and IncrementalHash.CreateHash/CreateHMAC throw CryptographicException for an unrecognized HashAlgorithmName, or ArgumentException for an empty one. Matching is case-sensitive with no whitespace trimming.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, string> TargetMethods = ImmutableDictionary.CreateRange(
    [
        new KeyValuePair<string, string>("HashData", "System.Security.Cryptography.CryptographicOperations"),
        new KeyValuePair<string, string>("HmacData", "System.Security.Cryptography.CryptographicOperations"),
        new KeyValuePair<string, string>("CreateHash", "System.Security.Cryptography.IncrementalHash"),
        new KeyValuePair<string, string>("CreateHMAC", "System.Security.Cryptography.IncrementalHash"),
    ]);

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
        if (!TargetMethods.TryGetValue(methodName, out var expectedType)) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var firstArg }, ..]) return;
        if (firstArg is not BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: [{ Expression: var nameArg }] } nameCreation)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(nameArg, ct).Value is not string text) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                IsStatic: true,
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != expectedType) return;

        if (context.SemanticModel.GetSymbolInfo(nameCreation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType.Name: "HashAlgorithmName",
            })
        {
            return;
        }

        try
        {
            using var hash = IncrementalHash.CreateHash(new HashAlgorithmName(text));
        }
        catch (System.Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), $"{containingType.Name}.{methodName}(...)", ex.GetType().Name, text));
        }
    }
}
