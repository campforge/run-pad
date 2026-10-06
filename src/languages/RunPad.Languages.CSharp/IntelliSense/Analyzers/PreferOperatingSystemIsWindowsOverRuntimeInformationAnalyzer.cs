using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто восемьдесят восьмой проход, batch) — own pick, not a Roslynator port.
// `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` (and the Linux/OSX/FreeBSD equivalents) asks
// via a general-purpose platform-comparison API for something the dedicated `OperatingSystem.IsWindows()`
// family (.NET 5+, well within this project's .NET 6 SDK floor) already answers directly, without
// constructing/comparing an OSPlatform value.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferOperatingSystemIsWindowsOverRuntimeInformationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0174";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer OperatingSystem.IsWindows() over RuntimeInformation.IsOSPlatform",
        messageFormat: "'RuntimeInformation.IsOSPlatform(OSPlatform.{0})' can be simplified to 'OperatingSystem.{1}()'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "OperatingSystem.IsWindows()/IsLinux()/IsMacOS()/IsFreeBSD() answer the same question as RuntimeInformation.IsOSPlatform(OSPlatform.X) directly, without an OSPlatform value.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, string> PlatformToMethod = ImmutableDictionary.CreateRange(
    [
        new KeyValuePair<string, string>("Windows", "IsWindows"),
        new KeyValuePair<string, string>("Linux", "IsLinux"),
        new KeyValuePair<string, string>("OSX", "IsMacOS"),
        new KeyValuePair<string, string>("FreeBSD", "IsFreeBSD"),
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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "IsOSPlatform" })
            return;
        if (invocation.ArgumentList.Arguments is not
            [{ Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: var platformName } platformAccess }])
        {
            return;
        }
        if (!PlatformToMethod.TryGetValue(platformName, out var suggestedMethod)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "IsOSPlatform" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Runtime.InteropServices.RuntimeInformation") return;

        if (context.SemanticModel.GetSymbolInfo(platformAccess, ct).Symbol is not IPropertySymbol { IsStatic: true } platformProperty)
            return;
        if (platformProperty.ContainingType?.ToDisplayString() != "System.Runtime.InteropServices.OSPlatform") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), platformName, suggestedMethod));
    }
}
