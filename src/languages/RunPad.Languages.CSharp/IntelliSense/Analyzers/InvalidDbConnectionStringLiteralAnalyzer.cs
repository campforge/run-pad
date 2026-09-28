using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста девяносто первый проход, batch) — own pick, not a Roslynator port; found by direct
// brainstorming, same pass that found RPAD0379-RPAD0382. `System.Runtime.InteropServices.OSPlatform.
// Create(string)` was also considered and DROPPED — confirmed empirically it only rejects the exact empty
// string (even a whitespace-only string is accepted), too narrow/trivial a check to be worth a dedicated
// analyzer, unlike this catalog's other format-validating candidates. `DbConnectionStringBuilder` has NO
// string constructor (only parameterless and `(bool useOdbcRules)`, confirmed via reflection) — the
// literal is instead assigned to the `ConnectionString` PROPERTY, so this analyzer follows the
// property-assignment shape established by InvalidCryptoAlgorithmKeySizeAnalyzer (RPAD0352) rather than
// the constructor-argument shape most of this catalog uses: dispatches on the RECEIVER EXPRESSION's own
// static type (not `property.ContainingType`), same lesson RPAD0352 learned the hard way. Scoped to the
// exact base type `System.Data.Common.DbConnectionStringBuilder`, not derived types like
// `SqlConnectionStringBuilder`/`OleDbConnectionStringBuilder` — those enforce additional provider-specific
// required keys the base type doesn't, a different validation surface not probed here. Confirmed
// empirically this is a single-exception-type situation: a key with no value ("=noKey"), an unterminated
// quoted value, or a bare token with no "=" at all all throw `System.ArgumentException`. Also confirmed the
// parser is lenient in ways worth noting for correct test literals: an empty string, and extra/trailing
// semicolons between pairs, are both accepted without throwing.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidDbConnectionStringLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0383";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "DbConnectionStringBuilder.ConnectionString literal is not well-formed",
        messageFormat: "'DbConnectionStringBuilder.ConnectionString = \"{0}\"' always throws ArgumentException at runtime — not a well-formed \"key=value\" connection string",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Assigning a malformed \"key=value; ...\" string to DbConnectionStringBuilder.ConnectionString throws ArgumentException.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ConnectionString" } access)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(assignment.Right, ct).Value is not string connectionString) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: "ConnectionString" } property)
            return;
        if (property.Type.SpecialType != SpecialType.System_String) return;

        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Data.Common.DbConnectionStringBuilder") return;

        try
        {
            _ = new DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch (ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), connectionString));
        }
    }
}
