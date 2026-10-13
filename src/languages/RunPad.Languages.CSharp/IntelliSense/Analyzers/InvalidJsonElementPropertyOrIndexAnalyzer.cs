using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. First analyzer
// in this catalog to touch `System.Text.Json` — an entirely fresh area (a prior candidate,
// InvalidJsonLiteralAnalyzer, was dropped earlier because the SDK's own `[StringSyntax("Json")]`
// analyzer already validates JSON string SYNTAX; this is a different, later question — given a
// SYNTACTICALLY VALID JSON literal, does a specific `.GetProperty(key)`/`[index]` access into its
// resulting `JsonElement` tree actually succeed). Uses the "run the real validator" technique already
// established in this catalog (e.g. crypto algorithms, `Guid`/`IPAddress`/`DateTime`)
// taken one step further: the ANALYZER ITSELF calls the real `System.Text.Json.JsonDocument.Parse` on the
// literal JSON text at analysis time (safe — pure, local, no I/O/OS handles, same reasoning as the crypto
// precedent) and walks the ACTUAL resulting `JsonElement` tree to answer "does this property/index exist,
// and is the receiver even the right JSON kind (object/array) to ask" — confirmed empirically via scratch
// `dotnet run` script: `.GetProperty(missingKey)` throws `KeyNotFoundException`; `.GetProperty(key)` on a
// non-object (array/scalar) root throws `InvalidOperationException` ("requires... Object... but...
// Array/Number"); `[literalIndex]` out of range (including negative) throws `IndexOutOfRangeException`;
// `[literalIndex]` on a non-array throws `InvalidOperationException` the same way. `TryGetProperty`
// deliberately NOT flagged — it's the documented no-throw alternative, same precedent as
// `Dictionary.TryGetValue` not being flagged by `DictionaryLiteralMissingKeyAnalyzer` (RPAD0312).
// Resolution is RECURSIVE (`TryResolveJsonElement`), so chains resolve naturally —
// `JsonDocument.Parse(json).RootElement.GetProperty("a").GetProperty("b")` resolves "a" first, then asks
// whether ITS resulting element (not the root) has "b" — confirmed empirically for both a valid nested
// chain and one that fails partway through. If the PARENT expression itself doesn't resolve (unknown
// shape, non-literal, or itself an invalid access), this analyzer silently bails rather than resolving
// against a fallback/guess — avoiding a confusing cascade of diagnostics down an already-broken chain
// (only the first broken link is ever reported, since only ITS containing invocation/element-access node
// has a resolvable parent). `JsonElement.Clone()` used to detach the resolved element from its
// `JsonDocument` before disposing it — confirmed empirically that a cloned element remains fully usable
// after its source document is disposed. Invalid JSON text (should not normally occur given the SDK's own
// `[StringSyntax("Json")]` coverage, but not guaranteed — e.g. a non-literal-checkable interpolation) is
// caught via `JsonException` and treated as unresolvable, not a diagnostic this analyzer reports itself.
// Scoped to `GetProperty`/the plain indexer only that change — the typed value getters
// (`GetInt32`/`GetString`/etc.) throwing `InvalidOperationException` for a `JsonElement` of the wrong
// `ValueKind` was a further, same-shaped case explicitly deferred at first.
// Extended to close that deferral, for all 17 zero-argument
// `JsonElement.GetXxx()` typed value getters (confirmed the exact set via reflection —
// `GetBoolean`/`GetString`/`GetBytesFromBase64`/`GetSByte`/`GetByte`/`GetInt16`/`GetUInt16`/`GetInt32`/
// `GetUInt32`/`GetInt64`/`GetUInt64`/`GetDouble`/`GetSingle`/`GetDecimal`/`GetDateTime`/
// `GetDateTimeOffset`/`GetGuid` — deliberately excluding `GetArrayLength`/`GetPropertyCount` (structural
// metadata, not value getters, and already effectively covered by the indexer/`GetProperty` checks
// above) and `GetRawText` (works for any `ValueKind`, never throws for that reason). Took the "run the
// real validator" technique one step further than a simple kind check: rather than hand-mapping each
// getter to its expected `ValueKind` (fragile, GetInt32-succeeds-or-fails depends on both KIND *and*
// numeric RANGE, not kind alone), `TryInvokeGetter` just calls the SAME real getter on the already-
// resolved real `JsonElement` inside a try/catch and reports whatever it actually throws — confirmed
// empirically that every failure mode across all 17 getters is one of exactly two exception types:
// `InvalidOperationException` for a `ValueKind` mismatch (e.g. `GetInt32()` on a JSON string), or
// `FormatException` for a value that's the right general kind but not parseable/in-range for that
// specific getter (e.g. `GetInt32()` on a JSON number too large for `Int32`, `GetGuid()` on a string
// that isn't GUID-shaped, `GetByte()` on `999`) — the catch is scoped to exactly those two types rather
// than a bare `catch (Exception)`, so anything unexpected still surfaces instead of being silently
// swallowed. The reported REASON text is the real exception's own `.Message` verbatim, not a hand-written
// paraphrase — guaranteed byte-accurate to what the user's own code would actually see at runtime, and
// avoids needing to hand-maintain 17 separate message templates.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidJsonElementPropertyOrIndexAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0364";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Property or index access is invalid for a parsed JSON literal",
        messageFormat: "'{0}' always throws {1} at runtime — {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "GetProperty(key) on a JsonElement parsed from a literal JSON string throws KeyNotFoundException when the key is absent, or InvalidOperationException when the element isn't a JSON object; the plain indexer throws IndexOutOfRangeException when the index is out of range, or InvalidOperationException when the element isn't a JSON array.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeGetProperty, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeElementAccess, SyntaxKind.ElementAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeTypedGetter, SyntaxKind.InvocationExpression);
    }

    private static readonly ImmutableHashSet<string> TypedGetterNames = ImmutableHashSet.Create(
        "GetBoolean", "GetString", "GetBytesFromBase64", "GetSByte", "GetByte", "GetInt16", "GetUInt16",
        "GetInt32", "GetUInt32", "GetInt64", "GetUInt64", "GetDouble", "GetSingle", "GetDecimal",
        "GetDateTime", "GetDateTimeOffset", "GetGuid");

    private static void AnalyzeTypedGetter(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } access
            || !TypedGetterNames.Contains(methodName))
        {
            return;
        }
        if (invocation.ArgumentList.Arguments.Count > 0) return;

        var ct = context.CancellationToken;
        if (TryResolveJsonElement(access.Expression, context.SemanticModel, ct) is not { } element) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName, ContainingType: { } containingType }
            || resolvedName != methodName
            || containingType.ToDisplayString() != "System.Text.Json.JsonElement")
        {
            return;
        }

        if (TryInvokeGetter(methodName, element) is not { } thrown) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), thrown.GetType().Name, thrown.Message));
    }

    private static System.Exception? TryInvokeGetter(string methodName, System.Text.Json.JsonElement element)
    {
        try
        {
            switch (methodName)
            {
                case "GetBoolean": _ = element.GetBoolean(); break;
                case "GetString": _ = element.GetString(); break;
                case "GetBytesFromBase64": _ = element.GetBytesFromBase64(); break;
                case "GetSByte": _ = element.GetSByte(); break;
                case "GetByte": _ = element.GetByte(); break;
                case "GetInt16": _ = element.GetInt16(); break;
                case "GetUInt16": _ = element.GetUInt16(); break;
                case "GetInt32": _ = element.GetInt32(); break;
                case "GetUInt32": _ = element.GetUInt32(); break;
                case "GetInt64": _ = element.GetInt64(); break;
                case "GetUInt64": _ = element.GetUInt64(); break;
                case "GetDouble": _ = element.GetDouble(); break;
                case "GetSingle": _ = element.GetSingle(); break;
                case "GetDecimal": _ = element.GetDecimal(); break;
                case "GetDateTime": _ = element.GetDateTime(); break;
                case "GetDateTimeOffset": _ = element.GetDateTimeOffset(); break;
                case "GetGuid": _ = element.GetGuid(); break;
            }
            return null;
        }
        catch (System.Exception ex) when (ex is System.InvalidOperationException or System.FormatException)
        {
            return ex;
        }
    }

    private static void AnalyzeGetProperty(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetProperty" } access)
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var keyArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(keyArg, ct).Value is not string key) return;
        if (TryResolveJsonElement(access.Expression, context.SemanticModel, ct) is not { } parent) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "GetProperty", ContainingType: { } containingType }
            || containingType.ToDisplayString() != "System.Text.Json.JsonElement")
        {
            return;
        }

        if (parent.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), "InvalidOperationException",
                $"the parsed JSON value is of kind {parent.ValueKind}, not Object"));
            return;
        }
        if (parent.TryGetProperty(key, out _)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), "KeyNotFoundException",
            $"the parsed JSON object has no property \"{key}\""));
    }

    private static void AnalyzeElementAccess(SyntaxNodeAnalysisContext context)
    {
        var elementAccess = (ElementAccessExpressionSyntax)context.Node;
        if (elementAccess.ArgumentList.Arguments is not [{ Expression: var indexArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(indexArg, ct).Value is not int index) return;
        if (TryResolveJsonElement(elementAccess.Expression, context.SemanticModel, ct) is not { } parent) return;

        if (context.SemanticModel.GetSymbolInfo(elementAccess, ct).Symbol is not IPropertySymbol { IsIndexer: true, ContainingType: { } containingType }
            || containingType.ToDisplayString() != "System.Text.Json.JsonElement")
        {
            return;
        }

        if (parent.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), elementAccess.ToString(), "InvalidOperationException",
                $"the parsed JSON value is of kind {parent.ValueKind}, not Array"));
            return;
        }
        if (index >= 0 && index < parent.GetArrayLength()) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), elementAccess.ToString(), "IndexOutOfRangeException",
            $"the parsed JSON array has only {parent.GetArrayLength()} element(s)"));
    }

    private static System.Text.Json.JsonElement? TryResolveJsonElement(ExpressionSyntax expression, SemanticModel semanticModel, CancellationToken ct)
    {
        if (expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "RootElement", Expression: InvocationExpressionSyntax parseInvocation })
        {
            if (parseInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" })
                return null;
            if (parseInvocation.ArgumentList.Arguments is not [{ Expression: var jsonArg }, ..]) return null;
            if (semanticModel.GetConstantValue(jsonArg, ct).Value is not string jsonText) return null;
            if (semanticModel.GetSymbolInfo(parseInvocation, ct).Symbol is not IMethodSymbol { Name: "Parse", ContainingType: { } parseType }
                || parseType.ToDisplayString() != "System.Text.Json.JsonDocument")
            {
                return null;
            }

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(jsonText);
                return document.RootElement.Clone();
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        if (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetProperty" } gpAccess } getPropertyInvocation
            && getPropertyInvocation.ArgumentList.Arguments is [{ Expression: var gpKeyArg }])
        {
            if (TryResolveJsonElement(gpAccess.Expression, semanticModel, ct) is not { } gpParent) return null;
            if (semanticModel.GetConstantValue(gpKeyArg, ct).Value is not string gpKey) return null;
            if (gpParent.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            return gpParent.TryGetProperty(gpKey, out var child) ? child : null;
        }

        if (expression is ElementAccessExpressionSyntax { ArgumentList.Arguments: [{ Expression: var eaIndexArg }] } nestedElementAccess)
        {
            if (TryResolveJsonElement(nestedElementAccess.Expression, semanticModel, ct) is not { } eaParent) return null;
            if (semanticModel.GetConstantValue(eaIndexArg, ct).Value is not int eaIndex) return null;
            if (eaParent.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            if (eaIndex < 0 || eaIndex >= eaParent.GetArrayLength()) return null;
            return eaParent[eaIndex];
        }

        return null;
    }
}
