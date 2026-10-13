using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate B from the twenty-fourth research subagent survey,
// re-verified independently before implementing. A fifth productive revisit of
// `System.Security.Cryptography.X509Certificates` (after X500DistinguishedNameBuilder/RPAD0472,
// SubjectAlternativeNameBuilder/RPAD0473, CertificateRevocationListBuilder/RPAD0477,
// X509SubjectKeyIdentifierExtension/RPAD0483). `X509BasicConstraintsExtension(bool
// certificateAuthority, bool hasPathLengthConstraint, int pathLengthConstraint, bool critical)` — a
// genuine two-joint-literal check (same shape precedent as `InvalidPipeOptionsConstructorAnalyzer`,
// RPAD0454): when `hasPathLengthConstraint` is the literal `true` AND `pathLengthConstraint` is a
// negative literal, always throws `ArgumentOutOfRangeException` regardless of the other two bool
// arguments (confirmed with -1 and int.MinValue). Confirmed `pathLengthConstraint` is completely
// IGNORED when `hasPathLengthConstraint` is `false` (a negative value there does NOT throw) — so both
// literals must be resolved and checked together, not just the int alone. Confirmed no
// `[SupportedOSPlatform]` gating and no side effects — pure in-memory extension construction, no
// certificate ever built. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidX509BasicConstraintsExtensionConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0485";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "X509BasicConstraintsExtension pathLengthConstraint is negative",
        messageFormat: "'new X509BasicConstraintsExtension(...)' always throws ArgumentOutOfRangeException at runtime — pathLengthConstraint ({0}) must be non-negative when hasPathLengthConstraint is true",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "X509BasicConstraintsExtension throws ArgumentOutOfRangeException when hasPathLengthConstraint is true and pathLengthConstraint is negative.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                ContainingType: { } containingType,
                Parameters: [{ Type.SpecialType: SpecialType.System_Boolean }, { Type.SpecialType: SpecialType.System_Boolean }, { Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Boolean }],
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension") return;
        if (creation.ArgumentList is not { Arguments: [_, { Expression: var hasConstraintArg }, { Expression: var lengthArg }, _] })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(hasConstraintArg, ct).Value is not bool hasPathLengthConstraint) return;
        if (!hasPathLengthConstraint) return;
        if (context.SemanticModel.GetConstantValue(lengthArg, ct).Value is not int pathLengthConstraint) return;
        if (pathLengthConstraint >= 0) return;

        try
        {
            _ = new X509BasicConstraintsExtension(true, true, pathLengthConstraint, false);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), pathLengthConstraint));
        }
    }
}
