using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста шестьдесят первый проход, batch) — own pick, not a Roslynator port. Same "run
// the real validator" theme as InvalidSemaphoreSlimConstructorArgumentAnalyzer (RPAD0319):
// `aes.KeySize = N`/`des.KeySize = N`/`tdes.KeySize = N`/`rsa.KeySize = N` (and `aes.BlockSize = N`/
// `des.BlockSize = N`/`tdes.BlockSize = N`) throw CryptographicException when `N` isn't one of the
// algorithm's own legal sizes (e.g. AES only accepts a 128/192/256-bit key and a fixed 128-bit block;
// DES only accepts a 64-bit key) — confirmed empirically via scratch `dotnet run` script for all four
// algorithm types rather than hand-deriving each one's legal-size set (which genuinely differs per
// algorithm). Constructs the REAL algorithm instance via `Create()` and sets the literal size on it
// inside a try/catch, same reasoning as this catalog's other "run the real validator" analyzers — a
// pure, local, side-effect-free operation (no cross-process visibility, unlike RPAD0322's `Semaphore`
// OS handle concern), immediately disposed. Scoped to a receiver whose STATIC type is exactly
// `Aes`/`DES`/`TripleDES`/`RSA` (e.g. from `Aes.Create()`), not any arbitrary `SymmetricAlgorithm`/
// `AsymmetricAlgorithm` subtype, since a different concrete algorithm would need its own probe.
// Self-correction caught via PROBE: `KeySize`/`BlockSize` are declared on the base
// `SymmetricAlgorithm`/`AsymmetricAlgorithm` classes and never overridden by `Aes`/`DES`/`TripleDES`/
// `RSA` — confirmed via a throwaway Roslyn symbol-resolution check that `GetSymbolInfo` on `aes.KeySize`
// resolves `ContainingType` to `SymmetricAlgorithm`, not `Aes` (the same class of surprise as RPAD0336's
// `MemoryStream.Read`/`Write`, just the opposite direction — there the derived type overrode the base;
// here it doesn't). The analyzer was initially written against `property.ContainingType`, which made it
// completely dead for every case; fixed by dispatching on the RECEIVER EXPRESSION's own static type
// instead (`GetTypeInfo(access.Expression).Type`), which correctly reflects the declared/inferred type
// at the call site regardless of which class actually declares the property.
// Extended in the триста восемьдесят первый проход (candidate surfaced by the same research subagent
// survey as RPAD0370-RPAD0373, проход 377) to also cover the static factory overload `RSA.Create(int
// keySizeInBits)` — a genuinely DIFFERENT syntax shape from the `rsa.KeySize = N` assignment this
// analyzer already covered (an invocation, not a property assignment), confirmed empirically to throw
// the same `CryptographicException` for the same reason. `Aes`/`DES`/`TripleDES` have no equivalent
// `Create(int)` overload (only the parameterless `Create()`), so this extension is RSA-specific.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCryptoAlgorithmKeySizeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0352";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Crypto algorithm key/block size is invalid",
        messageFormat: "'{0}.{1} = {2}' always throws CryptographicException at runtime — {2} is not a valid {1} for {0}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Assigning an unsupported value to Aes/DES/TripleDES/RSA's KeySize (or Aes/DES/TripleDES's BlockSize) property, or calling RSA.Create(int) with an unsupported key size, throws CryptographicException.");

    // Separate DiagnosticDescriptor, same DiagnosticId, for the RSA.Create(int) invocation shape added in
    // проход 381 — kept apart from `Rule` above rather than reshaping its "{0}.{1} = {2}" template (which
    // reads naturally only for an ASSIGNMENT) to also fit an invocation; the existing message text/shape
    // for the assignment case is preserved byte-for-byte, so no existing test needed updating.
    private static readonly DiagnosticDescriptor CreateRule = new(
        DiagnosticId,
        title: "Crypto algorithm key/block size is invalid",
        messageFormat: "'RSA.Create({0})' always throws CryptographicException at runtime — {0} is not a valid key size for RSA",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Assigning an unsupported value to Aes/DES/TripleDES/RSA's KeySize (or Aes/DES/TripleDES's BlockSize) property, or calling RSA.Create(int) with an unsupported key size, throws CryptographicException.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule, CreateRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
        context.RegisterSyntaxNodeAction(AnalyzeRsaCreate, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeRsaCreate(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Create" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var sizeArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(sizeArg, ct).Value is not int size) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Create",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_Int32 }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Security.Cryptography.RSA") return;

        if (!AlwaysThrows("System.Security.Cryptography.RSA", "KeySize", size)) return;

        context.ReportDiagnostic(Diagnostic.Create(CreateRule, invocation.GetLocation(), size));
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "KeySize" or "BlockSize" } access)
            return;
        var propertyName = access.Name.Identifier.Text;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(assignment.Right, ct).Value is not int size) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var resolvedName } property
            || resolvedName != propertyName)
        {
            return;
        }
        if (property.Type.SpecialType != SpecialType.System_Int32) return;

        // KeySize/BlockSize are declared on the base SymmetricAlgorithm/AsymmetricAlgorithm classes,
        // never overridden by Aes/DES/TripleDES/RSA — confirmed via a throwaway Roslyn symbol-resolution
        // check (not assumed), so the RECEIVER's own static type is what determines which concrete
        // algorithm to probe with, not property.ContainingType (which is always the base class).
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName is not ("System.Security.Cryptography.Aes" or "System.Security.Cryptography.DES"
            or "System.Security.Cryptography.TripleDES" or "System.Security.Cryptography.RSA"))
        {
            return;
        }
        if (propertyName == "BlockSize" && typeName == "System.Security.Cryptography.RSA") return;

        if (!AlwaysThrows(typeName, propertyName, size)) return;

        var shortTypeName = typeName[(typeName.LastIndexOf('.') + 1)..];
        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), shortTypeName, propertyName, size));
    }

    private static bool AlwaysThrows(string typeName, string propertyName, int size)
    {
        try
        {
            switch (typeName)
            {
                case "System.Security.Cryptography.Aes":
                    using (var aes = Aes.Create())
                    {
                        if (propertyName == "KeySize") aes.KeySize = size;
                        else aes.BlockSize = size;
                    }
                    break;
                case "System.Security.Cryptography.DES":
                    using (var des = DES.Create())
                    {
                        if (propertyName == "KeySize") des.KeySize = size;
                        else des.BlockSize = size;
                    }
                    break;
                case "System.Security.Cryptography.TripleDES":
                    using (var tdes = TripleDES.Create())
                    {
                        if (propertyName == "KeySize") tdes.KeySize = size;
                        else tdes.BlockSize = size;
                    }
                    break;
                case "System.Security.Cryptography.RSA":
                    using (var rsa = RSA.Create())
                    {
                        rsa.KeySize = size;
                    }
                    break;
            }
            return false;
        }
        catch (CryptographicException)
        {
            return true;
        }
    }
}
