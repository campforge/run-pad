using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RunPad.Format.RPad;

namespace RunPad.Languages.CSharp;

/// <summary>
/// The code a C# script gets when it becomes a Program: a <c>Main</c> skeleton for an empty script,
/// or its statements wrapped in <c>Main</c> — methods, types and usings stay outside it. Nothing
/// changes when the script already has a <c>Main</c>. The result marks the caret with <c>${0}</c>,
/// as snippets do.
/// </summary>
internal static class ProgramTemplate
{
    public const string Comment = "// Other methods, fields and types used by Main go here.";
    private const string Indent = "    ";

    public static string Skeleton { get; } = $"void Main()\n{{\n{Indent}${{0}}\n}}\n\n{Comment}\n";

    public static string? CodeForKind(string code, RpadKind kind)
    {
        if (kind != RpadKind.Program || ProgramEntryPointDetector.DetectMain(code).Found) return null;
        if (string.IsNullOrWhiteSpace(code)) return Skeleton;

        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();
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
            ? [Indent + "${0}"]
            : body.Split('\n').Select((line, i) => (i == 0 ? Indent + "${0}" : line.Length == 0 ? "" : Indent) + line.TrimEnd());

        var result = Lf(usings).TrimEnd('\n');
        if (result.Length > 0) result += "\n\n";
        result += $"{header}\n{{\n{string.Join("\n", bodyLines)}\n}}\n";
        declarations = Lf(declarations).Trim('\n');
        result += "\n" + (declarations.Length > 0 ? declarations : Comment) + "\n";
        return result;
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n");

    // An await that makes Main itself async: not one inside a lambda or local function.
    private static bool AwaitsDirectly(GlobalStatementSyntax statement)
        => statement.DescendantTokens().Any(t =>
            t.IsKind(SyntaxKind.AwaitKeyword)
            && t.Parent?.FirstAncestorOrSelf<SyntaxNode>(n =>
                n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) is null);
}
