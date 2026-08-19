using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (433-й проход, batch) — candidate #4 from the fifth research subagent survey, re-verified
// independently before implementing. A string literal containing an embedded `'\0'` (NUL) character
// passed as a path to `Path.GetFullPath(string)`, `new FileInfo(string)`, `new DirectoryInfo(string)`, or
// `Directory.CreateDirectory(string)` — confirmed empirically, matching the agent's report: all four
// throw `ArgumentException` ("Null character in path.") from pure string validation, no filesystem I/O
// needed to trigger it. Confirmed via `Path.GetInvalidPathChars()` that NUL is the ONLY universally-
// invalid path character across platforms (Linux returns just `[0]`; Windows' broader control-char set is
// Windows-specific) — scoping strictly to an embedded NUL check is the only platform-invariant condition
// here, avoiding the exact trap that made `ECCurve`/`SecurityIdentifier` unusable earlier in this catalog.
// Deliberately scoped to only these four call shapes (not the agent's full list, which also included
// `new StreamReader`/`new StreamWriter`/`File.ReadAllText`/`Path.GetRelativePath`) — those have more
// overloads whose shapes risk ambiguous matches, left for a possible follow-up rather than rushed here.
// Confirmed the sibling APIs the agent flagged as NOT throwing for embedded NUL (`Path.Combine`,
// `Path.GetDirectoryName`, `File.Exists`) are correctly excluded by this analyzer's method-name dispatch.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidPathEmbeddedNulLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0423";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Path argument contains an embedded NUL character",
        messageFormat: "'{0}' always throws ArgumentException at runtime — the path literal contains an embedded NUL ('\\0') character",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Path.GetFullPath/new FileInfo/new DirectoryInfo/Directory.CreateDirectory throw ArgumentException for a path string containing an embedded NUL character — the only path character confirmed invalid across all platforms.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        ExpressionSyntax? pathArg;
        string callText;
        ISymbol? symbol;
        var ct = context.CancellationToken;

        switch (context.Node)
        {
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetFullPath" } } invocation:
                if (invocation.ArgumentList.Arguments is not [{ Expression: var fullPathArg }]) return;
                pathArg = fullPathArg;
                callText = "Path.GetFullPath(...)";
                symbol = context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol;
                break;
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "CreateDirectory" } } invocation:
                if (invocation.ArgumentList.Arguments is not [{ Expression: var createDirArg }]) return;
                pathArg = createDirArg;
                callText = "Directory.CreateDirectory(...)";
                symbol = context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol;
                break;
            case BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: [{ Expression: var ctorArg }] } creation:
                pathArg = ctorArg;
                symbol = context.SemanticModel.GetSymbolInfo(creation, ct).Symbol;
                callText = symbol is IMethodSymbol { ContainingType: { } t } ? $"new {t.Name}(...)" : "";
                break;
            default:
                return;
        }

        if (context.SemanticModel.GetConstantValue(pathArg, ct).Value is not string path) return;
        if (!path.Contains('\0')) return;

        if (symbol is not IMethodSymbol method) return;
        var isMatch = method switch
        {
            { MethodKind: MethodKind.Ordinary, IsStatic: true, Name: "GetFullPath", Parameters: [{ Type.SpecialType: SpecialType.System_String }] } =>
                method.ContainingType?.ToDisplayString() == "System.IO.Path",
            { MethodKind: MethodKind.Ordinary, IsStatic: true, Name: "CreateDirectory", Parameters: [{ Type.SpecialType: SpecialType.System_String }] } =>
                method.ContainingType?.ToDisplayString() == "System.IO.Directory",
            { MethodKind: MethodKind.Constructor, Parameters: [{ Type.SpecialType: SpecialType.System_String }] } =>
                method.ContainingType?.ToDisplayString() is "System.IO.FileInfo" or "System.IO.DirectoryInfo",
            _ => false,
        };
        if (!isMatch) return;

        try
        {
            switch (method.ContainingType!.ToDisplayString())
            {
                case "System.IO.Path":
                    _ = Path.GetFullPath(path);
                    break;
                case "System.IO.Directory":
                    Directory.CreateDirectory(path);
                    break;
                case "System.IO.FileInfo":
                    _ = new FileInfo(path);
                    break;
                case "System.IO.DirectoryInfo":
                    _ = new DirectoryInfo(path);
                    break;
            }
        }
        catch (ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), callText));
        }
    }
}
