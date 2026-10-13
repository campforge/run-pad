using System.Collections.Immutable;
using System.Net.Http;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidates #2/#3 from the thirteenth research subagent survey,
// re-verified independently before implementing. `HttpClient.MaxResponseContentBufferSize` (long), and
// `HttpClientHandler`/`SocketsHttpHandler`'s shared `MaxConnectionsPerServer`/`MaxAutomaticRedirections`/
// `MaxResponseHeadersLength` (all int) — every one of these throws `ArgumentOutOfRangeException` with the
// identical message shape ("must be a non-negative and non-zero value") for a value ≤ 0, confirmed
// empirically on both `HttpClientHandler` and `SocketsHttpHandler` for all three shared property names.
// Confirmed the range check fires even on an already-used handler/client (state-invariant for
// literal-only detection, same reasoning already established for RPAD0446's `HttpClient.Timeout`).
// Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. No side effects — construction and assignment never
// touch the network. One analyzer covers all four (property, containing-type) combinations, mirroring how
// `InvalidXmlWriterSettingsPropertyAnalyzer` (RPAD0444) already covers multiple sibling properties on one
// type — extended here to multiple sibling TYPES sharing the identical property name/boundary.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidHttpNonPositiveLimitPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0447";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Http configuration property must be positive",
        messageFormat: "'{0}.{1} = {2}' always throws ArgumentOutOfRangeException at runtime — {1} must be greater than zero",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "HttpClient.MaxResponseContentBufferSize and HttpClientHandler/SocketsHttpHandler's MaxConnectionsPerServer/MaxAutomaticRedirections/MaxResponseHeadersLength all throw ArgumentOutOfRangeException for a value that isn't strictly positive.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> HandlerPropertyNames = ImmutableHashSet.Create(
        "MaxConnectionsPerServer", "MaxAutomaticRedirections", "MaxResponseHeadersLength");

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

        var isHttpClientBufferSize = propertyName == "MaxResponseContentBufferSize";
        if (!isHttpClientBufferSize && !HandlerPropertyNames.Contains(propertyName)) return;

        var ct = context.CancellationToken;
        var constant = context.SemanticModel.GetConstantValue(assignment.Right, ct);
        if (constant.Value is not (int or long)) return;
        var value = System.Convert.ToInt64(constant.Value);

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var symbolName })
            return;
        if (symbolName != propertyName) return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName is null) return;

        var shortTypeName = typeName switch
        {
            "System.Net.Http.HttpClient" when isHttpClientBufferSize => "HttpClient",
            "System.Net.Http.HttpClientHandler" when !isHttpClientBufferSize => "HttpClientHandler",
            "System.Net.Http.SocketsHttpHandler" when !isHttpClientBufferSize => "SocketsHttpHandler",
            _ => null,
        };
        if (shortTypeName is null) return;

        if (!AlwaysThrows(shortTypeName, propertyName, value)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), shortTypeName, propertyName, value));
    }

    private static bool AlwaysThrows(string typeName, string propertyName, long value)
    {
        try
        {
            switch (typeName)
            {
                case "HttpClient":
                    using (var client = new HttpClient())
                        client.MaxResponseContentBufferSize = value;
                    break;
                case "HttpClientHandler":
                    using (var handler = new HttpClientHandler())
                        SetHandlerProperty(handler, propertyName, (int)value);
                    break;
                case "SocketsHttpHandler":
                    using (var handler = new SocketsHttpHandler())
                        SetSocketsHandlerProperty(handler, propertyName, (int)value);
                    break;
            }
            return false;
        }
        catch (System.ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    private static void SetHandlerProperty(HttpClientHandler handler, string propertyName, int value)
    {
        switch (propertyName)
        {
            case "MaxConnectionsPerServer": handler.MaxConnectionsPerServer = value; break;
            case "MaxAutomaticRedirections": handler.MaxAutomaticRedirections = value; break;
            case "MaxResponseHeadersLength": handler.MaxResponseHeadersLength = value; break;
        }
    }

    private static void SetSocketsHandlerProperty(SocketsHttpHandler handler, string propertyName, int value)
    {
        switch (propertyName)
        {
            case "MaxConnectionsPerServer": handler.MaxConnectionsPerServer = value; break;
            case "MaxAutomaticRedirections": handler.MaxAutomaticRedirections = value; break;
            case "MaxResponseHeadersLength": handler.MaxResponseHeadersLength = value; break;
        }
    }
}
