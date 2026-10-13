using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Security.Cryptography.X509Certificates.
// X509Certificate2Collection.Find(X509FindType findType, object findValue, bool validOnly)` — a ninth
// productive revisit of `X509Certificates` (after RPAD0472/0473/0477/0483/0485/0488/0496).
// `X509FindType` is `int`-backed (confirmed via `Enum.GetUnderlyingType`), valid members are exactly
// `0..14`. Confirmed empirically: an out-of-range `findType` throws `System.Security.Cryptography.
// CryptographicException` ("Invalid find type.") — but ONLY when `findValue` is non-null; a null
// `findValue` triggers `ArgumentNullException` first (checked before the enum-validity check),
// independent of `findType`. Confirmed this holds on both empty and non-empty collections — the enum
// check runs before any element is examined. This analyzer therefore only flags the call when the
// `findValue` argument syntactically isn't the literal `null` (a non-literal expression is assumed
// non-null for probing purposes, using a placeholder non-null value for the actual probe call, same
// "irrelevant argument gets a safe placeholder" technique already used elsewhere in this catalog, e.g.
// `X500DistinguishedNameBuilder.Add`'s dummy `"v"` value argument). No `[SupportedOSPlatform]` gating,
// no side effects — pure in-memory validation, no OS/crypto calls triggered before the throw.
// Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidX509Certificate2CollectionFindAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0500";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "X509Certificate2Collection.Find findType is invalid",
        messageFormat: "'X509Certificate2Collection.Find(...)' always throws CryptographicException at runtime — the value is not a defined X509FindType member",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "X509Certificate2Collection.Find throws CryptographicException when findType is outside the defined X509FindType range (0-14) and findValue isn't null.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Find" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Find",
                ContainingType: { } containingType,
                Parameters: [{ Type.Name: "X509FindType" }, _, { Type.SpecialType: SpecialType.System_Boolean }],
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Security.Cryptography.X509Certificates.X509Certificate2Collection") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var findTypeArg }, { Expression: var findValueArg }, _])
            return;
        // A literal `null` findValue changes the thrown exception to ArgumentNullException — bail
        // rather than misattribute.
        if (findValueArg.IsKind(SyntaxKind.NullLiteralExpression)) return;

        if (context.SemanticModel.GetConstantValue(findTypeArg, ct).Value is not int findTypeRaw) return;

        var collection = new X509Certificate2Collection();
        try
        {
            collection.Find((X509FindType)findTypeRaw, "RunPadAnalyzerProbe", false);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
        }
        catch
        {
            // Any other exception means the placeholder findValue tripped a different, unrelated
            // check first — don't misattribute it to findType.
        }
    }
}
