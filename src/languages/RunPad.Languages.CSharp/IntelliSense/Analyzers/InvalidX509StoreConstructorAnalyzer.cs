using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// A twelfth productive revisit of `X509Certificates`
// (after RPAD0472/0473/0477/0483/0485/0488/0496/0500/0507). `X509Store`'s `StoreName`/`StoreLocation`
// enum-taking constructors — confirmed empirically both enums are `int`-backed
// (`Enum.GetUnderlyingType`) and contiguous: `StoreName` valid range is `1..8`
// (AddressBook..TrustedPublisher, 0 and 9+ both throw); `StoreLocation` valid range is `1..2`
// (CurrentUser/LocalMachine, 0 and 3+ both throw). Both throw `ArgumentException` ("Illegal enum
// value: storeName."/"...storeLocation.") synchronously from the constructor itself — confirmed no OS
// certificate-store access happens at construction time (only `.Open()`, never called by this
// analyzer, touches the real store). The third, `[Flags]`-typed `OpenFlags` parameter is confirmed NOT
// validated at all (`(OpenFlags)int.MaxValue`/`(OpenFlags)(-1)` both construct successfully) — not
// flagged. No `[SupportedOSPlatform]` gating. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidX509StoreConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0508";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "X509Store constructor enum argument is out of range",
        messageFormat: "'new X509Store(...)' always throws ArgumentException at runtime — the value is not a defined {0} member",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "X509Store constructors throw ArgumentException when storeName is outside [1, 8] (StoreName) or storeLocation is outside [1, 2] (StoreLocation).");

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
            } ctor)
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Security.Cryptography.X509Certificates.X509Store") return;
        if (creation.ArgumentList is not { } argumentList) return;

        var ct = context.CancellationToken;
        int? storeNameRaw = null;
        int? storeLocationRaw = null;
        for (var i = 0; i < ctor.Parameters.Length && i < argumentList.Arguments.Count; i++)
        {
            var paramTypeName = ctor.Parameters[i].Type.Name;
            var value = context.SemanticModel.GetConstantValue(argumentList.Arguments[i].Expression, ct).Value as int?;
            if (paramTypeName == "StoreName") storeNameRaw = value;
            else if (paramTypeName == "StoreLocation") storeLocationRaw = value;
        }
        if (storeNameRaw is null && storeLocationRaw is null) return;

        try
        {
            if (storeNameRaw is not null && storeLocationRaw is not null)
                _ = new X509Store((StoreName)storeNameRaw, (StoreLocation)storeLocationRaw);
            else if (storeNameRaw is not null)
                _ = new X509Store((StoreName)storeNameRaw);
            else
                _ = new X509Store((StoreLocation)storeLocationRaw!.Value);
        }
        catch (System.ArgumentException ex)
        {
            var enumName = ex.Message.Contains("storeName") ? "StoreName" : "StoreLocation";
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), enumName));
        }
    }
}
