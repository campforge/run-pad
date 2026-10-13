using System.Collections.Immutable;
using System.Numerics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Numerics.Matrix4x4`'s six
// `CreatePerspective*` static methods (`CreatePerspective`/`CreatePerspectiveLeftHanded`/
// `CreatePerspectiveOffCenter`/`CreatePerspectiveOffCenterLeftHanded`/`CreatePerspectiveFieldOfView`/
// `CreatePerspectiveFieldOfViewLeftHanded`) — first touch of `System.Numerics` in this catalog.
// Confirmed empirically: `nearPlaneDistance <= 0`, `farPlaneDistance <= 0`, or `nearPlaneDistance >=
// farPlaneDistance` all throw `ArgumentOutOfRangeException` (attributed to whichever check fires
// first — `nearPlaneDistance` in every observed case); confirmed `width`/`height`/off-center
// `left`/`right`/`bottom`/`top` are NOT validated at all (0/negative accepted silently) so this
// analyzer only extracts the two plane-distance arguments, never the others. The two `FieldOfView`
// overloads additionally validate `fieldOfView` strictly in `(0, π)` (confirmed 0/negative/π/π+ε all
// throw, π-ε and NaN's `<= 0` branch also throw). Confirmed the sibling `CreateOrthographic`/
// `CreateOrthographicOffCenter` methods have NO validation at all (declined, dead end). No
// `[SupportedOSPlatform]` gating, no side effects — pure value-type math. Culture-independent (no
// formatting involved, plain float comparisons).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidMatrix4x4PerspectiveArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0484";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Matrix4x4.CreatePerspective* argument always throws",
        messageFormat: "'Matrix4x4.{0}(...)' always throws ArgumentOutOfRangeException at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Matrix4x4's CreatePerspective/CreatePerspectiveLeftHanded/CreatePerspectiveOffCenter/CreatePerspectiveOffCenterLeftHanded/CreatePerspectiveFieldOfView/CreatePerspectiveFieldOfViewLeftHanded throw ArgumentOutOfRangeException when nearPlaneDistance/farPlaneDistance aren't positive with near < far, or (for the FieldOfView overloads) fieldOfView isn't strictly between 0 and pi.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> MethodNames = ImmutableHashSet.Create(
        "CreatePerspective", "CreatePerspectiveLeftHanded",
        "CreatePerspectiveOffCenter", "CreatePerspectiveOffCenterLeftHanded",
        "CreatePerspectiveFieldOfView", "CreatePerspectiveFieldOfViewLeftHanded");

    private static readonly ImmutableHashSet<string> FieldOfViewMethods = ImmutableHashSet.Create(
        "CreatePerspectiveFieldOfView", "CreatePerspectiveFieldOfViewLeftHanded");

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
        if (!MethodNames.Contains(methodName)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                IsStatic: true,
                ContainingType: { } containingType,
            } method)
        {
            return;
        }
        if (method.Name != methodName) return;
        if (containingType.ToDisplayString() != "System.Numerics.Matrix4x4") return;

        var args = invocation.ArgumentList.Arguments;
        var isFov = FieldOfViewMethods.Contains(methodName);
        // The last two float parameters are always nearPlaneDistance/farPlaneDistance across all six
        // overloads; the FieldOfView overloads' first parameter is fieldOfView.
        if (args.Count < 4) return;
        var nearArg = args[^2].Expression;
        var farArg = args[^1].Expression;

        if (ToSingleConstant(context.SemanticModel.GetConstantValue(nearArg, ct)) is not { } near) return;
        if (ToSingleConstant(context.SemanticModel.GetConstantValue(farArg, ct)) is not { } far) return;

        float? fov = null;
        if (isFov)
        {
            if (ToSingleConstant(context.SemanticModel.GetConstantValue(args[0].Expression, ct)) is not { } fovValue) return;
            fov = fovValue;
        }

        try
        {
            switch (methodName)
            {
                case "CreatePerspective": _ = Matrix4x4.CreatePerspective(1, 1, near, far); break;
                case "CreatePerspectiveLeftHanded": _ = Matrix4x4.CreatePerspectiveLeftHanded(1, 1, near, far); break;
                case "CreatePerspectiveOffCenter": _ = Matrix4x4.CreatePerspectiveOffCenter(-1, 1, -1, 1, near, far); break;
                case "CreatePerspectiveOffCenterLeftHanded": _ = Matrix4x4.CreatePerspectiveOffCenterLeftHanded(-1, 1, -1, 1, near, far); break;
                case "CreatePerspectiveFieldOfView": _ = Matrix4x4.CreatePerspectiveFieldOfView(fov!.Value, 1, near, far); break;
                default: _ = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(fov!.Value, 1, near, far); break;
            }
        }
        catch (System.ArgumentOutOfRangeException ex)
        {
            var reason = ex.ParamName switch
            {
                "fieldOfView" => "fieldOfView must be strictly between 0 and pi",
                "farPlaneDistance" => "farPlaneDistance must be positive",
                _ => "nearPlaneDistance must be positive and less than farPlaneDistance",
            };
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, reason));
        }
    }

    // A plain integer literal ("10") implicitly converted to a float parameter constant-folds as a
    // boxed int, not a boxed float — GetConstantValue reflects the LITERAL's own type, not the
    // parameter's. Normalize both possibilities rather than assuming callers always write "10f".
    private static float? ToSingleConstant(Optional<object?> constant) => constant.Value switch
    {
        float f => f,
        int i => i,
        double d => (float)d,
        _ => null,
    };
}
