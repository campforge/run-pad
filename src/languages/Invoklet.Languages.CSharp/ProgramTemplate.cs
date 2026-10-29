using Invoklet.Format.MdRun;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Invoklet.Languages.CSharp;

/// <summary>
/// The code a C# script gets when it becomes a Program: a <c>Main</c> skeleton for an empty script,
/// or its statements wrapped in <c>Main</c> — methods, types and usings stay outside it. Nothing
/// changes when the script already has a <c>Main</c>. The result marks the caret with <c>${0}</c>,
/// as snippets do.
/// </summary>
internal static class ProgramTemplate
{
    public const string Comment = "// Other methods, fields and types used by Main go here.";
    private const string DefaultIndent = "    ";

    // No target framework is known when the kind changes: the symbols of the runtime Invoklet runs on,
    // the same ones the editor uses until it knows the script's target.
    private static readonly CSharpParseOptions ParseOptions =
        Compiler.CSharpScriptCompiler.ParseOptions(Environment.Version.Major, Environment.Version.Minor);

    public static string Skeleton { get; } = SkeletonWith(DefaultIndent);

    private static string SkeletonWith(string indent) => $"void Main()\n{{\n{indent}${{0}}\n}}\n\n{Comment}\n";

    // <indent> is one level of the editor's indentation, used for the lines the template moves into Main.
    public static string? CodeForKind(string code, MdRunKind kind, string indent = DefaultIndent)
    {
        if (kind != MdRunKind.Program || ProgramEntryPointDetector.DetectMain(code, ParseOptions).Found) return null;
        // A classic program (a class with a static Main, no top-level statements) runs as it is.
        if (!string.IsNullOrWhiteSpace(code) && ProgramEntryPointDetector.IsCompiledAsWritten(code, ParseOptions)
            && HasStaticMain(code)) return null;
        if (string.IsNullOrWhiteSpace(code)) return SkeletonWith(indent);

        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot();
        var usings = string.Concat(root.Externs.Select(e => e.ToFullString()))
                     + string.Concat(root.Usings.Select(u => u.ToFullString()));
        var statements = root.Members.OfType<GlobalStatementSyntax>()
            .Where(g => g.Statement is not LocalFunctionStatementSyntax)
            .ToList();
        // Local functions become methods of the program; types and namespaces stay as they are.
        var declarations = string.Concat(root.Members.Except(statements).Select(m => m.ToFullString()))
                           + root.EndOfFileToken.LeadingTrivia.ToFullString();

        var body = string.Concat(statements.Select(s => s.ToFullString())).Replace("\r\n", "\n").Trim('\n');
        var header = statements.Any(AwaitsDirectly) ? "async Task Main()" : "void Main()";
        var bodyLines = body.Length == 0
            ? [indent + "${0}"]
            : body.Split('\n').Select((line, i) => (i == 0 ? indent + "${0}" : line.Length == 0 ? "" : indent) + line.TrimEnd());

        var result = Lf(usings).TrimEnd('\n');
        if (result.Length > 0) result += "\n\n";
        result += $"{header}\n{{\n{string.Join("\n", bodyLines)}\n}}\n";
        declarations = Lf(declarations).Trim('\n');
        result += "\n" + (declarations.Length > 0 ? declarations : Comment) + "\n";
        return result;
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n");

    // A classic program's entry point: a static Main method declared in some type.
    private static bool HasStaticMain(string code) => CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot()
        .DescendantNodes().OfType<MethodDeclarationSyntax>()
        .Any(m => m.Identifier.Text == "Main" && m.Modifiers.Any(SyntaxKind.StaticKeyword));

    // An await that makes Main itself async: not one inside a lambda or local function.
    private static bool AwaitsDirectly(GlobalStatementSyntax statement)
        => statement.DescendantTokens().Any(t =>
            t.IsKind(SyntaxKind.AwaitKeyword)
            && t.Parent?.FirstAncestorOrSelf<SyntaxNode>(n =>
                n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) is null);
}
