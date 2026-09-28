using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (470-й проход, batch) — candidate #2 from the thirty-third research subagent survey,
// re-verified independently before implementing. `System.Security.Cryptography.X509Certificates.
// X509CertificateLoader.LoadPkcs12(byte[], string?, X509KeyStorageFlags, Pkcs12LoaderLimits?)` and its
// sibling `LoadPkcs12Collection` — the modern, non-obsolete replacement for the `[Obsolete]`/SYSLIB0057
// `X509Certificate2` constructors. Confirmed empirically: `X509KeyStorageFlags` is `int`-backed
// (`Enum.GetUnderlyingType`); an invalid value throws `ArgumentException` (param `keyStorageFlags`,
// POPULATED this time, unlike `X509Store`'s enum checks) — confirmed independent of `data`'s actual
// content (tested with both an empty and a non-empty garbage `byte[]`). Two distinct invalid-flag
// shapes: both `PersistKeySet`(16) and `EphemeralKeySet`(32) bits set together ("may not be specified
// together"), or any bit set outside the defined `0x3F` mask (negative values included, since they set
// high bits). Confirmed `UserKeySet|MachineKeySet`(3) does NOT trip this check — falls through to real
// ASN.1 parsing instead, so this analyzer's probe uses an empty `byte[]` and treats a resulting
// `CryptographicException` (parsing failure) as "not our concern here", only flagging on
// `ArgumentException`. CRITICAL SCOPE NOTE confirmed via direct testing: the sibling
// `ReadOnlySpan<byte>`/`ReadOnlySpan<char>`-typed overload does NOT perform this eager flags validation
// at all (goes straight to ASN.1 parsing regardless of flags) — this analyzer is deliberately scoped
// to the `byte[]`/`string?`-typed overloads only, never the span overload. No `[SupportedOSPlatform]`
// gating beyond `[UnsupportedOSPlatform("browser")]` (fine for RunPad's desktop targets). Confirmed
// invariant across en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidX509CertificateLoaderKeyStorageFlagsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0510";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "X509CertificateLoader keyStorageFlags is invalid",
        messageFormat: "'X509CertificateLoader.{0}(...)' always throws ArgumentException at runtime — the keyStorageFlags value is invalid",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "X509CertificateLoader.LoadPkcs12/LoadPkcs12Collection (the byte[]-based overloads) throw ArgumentException when keyStorageFlags sets both PersistKeySet and EphemeralKeySet, or sets any bit outside the defined flags mask.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } access)
            return;
        var isCollection = methodName == "LoadPkcs12Collection";
        if (!isCollection && methodName != "LoadPkcs12") return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                IsStatic: true,
                ContainingType: { } containingType,
                Parameters: [{ Type: IArrayTypeSymbol }, _, { Type.Name: "X509KeyStorageFlags" }, ..],
            } method)
        {
            return;
        }
        if (method.Name != methodName) return;
        if (containingType.ToDisplayString() != "System.Security.Cryptography.X509Certificates.X509CertificateLoader") return;
        if (invocation.ArgumentList.Arguments is not [_, _, { Expression: var flagsArg }, ..]) return;
        if (context.SemanticModel.GetConstantValue(flagsArg, ct).Value is not int flagsRaw) return;

        try
        {
            var emptyData = System.Array.Empty<byte>();
            if (isCollection) _ = X509CertificateLoader.LoadPkcs12Collection(emptyData, (string?)null, (X509KeyStorageFlags)flagsRaw);
            else _ = X509CertificateLoader.LoadPkcs12(emptyData, (string?)null, (X509KeyStorageFlags)flagsRaw);
        }
        catch (System.ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
        }
        catch
        {
            // Any other exception (e.g. CryptographicException from ASN.1 parsing of the empty/dummy
            // data) means the flags value is fine and something else tripped first — not our concern.
        }
    }
}
