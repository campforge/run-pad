using Invoklet.Contracts.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Invoklet.Languages.CSharp.IntelliSense;

/// <summary>Checks the condition of a breakpoint against the code at its line, without running it or touching the editor's workspace,
/// and suggests what could be typed next.</summary>
internal sealed class BreakpointConditionAnalyzer(Func<string, Document> syncDocument, ScriptDocumentLayout layout)
{
    private readonly Func<string, Document> _syncDocument = syncDocument;
    private readonly ScriptDocumentLayout _layout = layout;

    internal async Task<BreakpointConditionAnalysis?> AnalyzeBreakpointConditionAsync(string code, int position,
        string expression, CancellationToken cancellationToken = default)
    {
        var document = _syncDocument(code);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model is null) return null;
        var pos = _layout.ToDocumentOffset(code, Math.Clamp(position, 0, code.Length));
        var parsed = SyntaxFactory.ParseExpression(expression);
        string? error = null;
        if (!string.IsNullOrWhiteSpace(expression))
        {
            var syntaxError = parsed.GetDiagnostics().FirstOrDefault(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            if (syntaxError is not null) error = syntaxError.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
            else
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var statement = root?.FindToken(pos).Parent?.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
                if (statement is null) error = "Choose a line inside executable code.";
                else
                {
                    // Bind in a forked document, never execute the expression or modify the editor workspace.
                    var mark = new SyntaxAnnotation();
                    var condition = parsed.WithAdditionalAnnotations(mark);
                    var probe = SyntaxFactory.IfStatement(condition, SyntaxFactory.Block());
                    var fork = document.WithSyntaxRoot(root!.ReplaceNode(statement, SyntaxFactory.Block(probe, statement)));
                    var forkRoot = await fork.GetSyntaxRootAsync(cancellationToken);
                    var forkModel = await fork.GetSemanticModelAsync(cancellationToken);
                    var bound = forkRoot!.GetAnnotatedNodes(mark).Single();
                    error = forkModel!.GetDiagnostics(cancellationToken: cancellationToken)
                        .FirstOrDefault(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
                            && d.Location.SourceSpan.IntersectsWith(bound.Span))
                        ?.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
                    if (error is null && forkModel.GetTypeInfo(bound, cancellationToken).ConvertedType?.SpecialType != SpecialType.System_Boolean)
                        error = "The condition must be a Boolean expression.";
                }
            }
        }
        var end = expression.Length;
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(expression[start - 1]) || expression[start - 1] == '_')) start--;
        var prefix = expression[start..];
        IEnumerable<ISymbol> symbols = model.LookupSymbols(pos);
        if (start > 0 && expression[start - 1] == '.')
        {
            var receiver = SyntaxFactory.ParseExpression(expression[..(start - 1)]);
            var type = model.GetSpeculativeTypeInfo(pos, receiver, SpeculativeBindingOption.BindAsExpression).Type;
            symbols = type is null ? [] : model.LookupSymbols(pos, container: type);
        }
        var suggestions = symbols.Where(s => s.CanBeReferencedByName && !s.IsImplicitlyDeclared)
            .Select(s => s.Name).Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).Take(40).ToArray();
        return new(error, suggestions);
    }
}
