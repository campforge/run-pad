using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RunPad.Languages.CSharp;

/// <summary>
/// Keeps types at namespace scope in member-style programs with extension methods. Only synthetic
/// braces and partial-class headers are inserted; the original characters stay in their original
/// order, and no lines are inserted. The same projection is used by the compiler and editor.
/// </summary>
internal sealed class ProgramBodyProjection
{
    private sealed record Insertion(int Offset, string Text);
    private readonly IReadOnlyList<Insertion> _insertions;
    public string Original { get; }
    public string Text { get; }
    public bool HasChanges => _insertions.Count != 0;

    private ProgramBodyProjection(string code, IReadOnlyList<Insertion> insertions)
    {
        Original = code;
        _insertions = insertions;
        var builder = new StringBuilder();
        var offset = 0;
        foreach (var insertion in insertions)
        {
            builder.Append(code, offset, insertion.Offset - offset).Append(insertion.Text);
            offset = insertion.Offset;
        }
        Text = builder.Append(code, offset, code.Length - offset).ToString();
    }

    public static ProgramBodyProjection Identity(string code) => new(code, []);

    public static ProgramBodyProjection Create(string code, CSharpParseOptions options)
    {
        var root = CSharpSyntaxTree.ParseText(code, options).GetCompilationUnitRoot();
        if (!root.Members.OfType<GlobalStatementSyntax>().Any()
            || !root.Members.Any(ContainsExtensionClass)) return Identity(code);

        // Types used as extension receivers must share the extension class's namespace scope. Types
        // nested inside one of these declarations stay nested: only compilation-unit members move.
        var declarations = root.Members.Where(m => m is BaseTypeDeclarationSyntax
            or DelegateDeclarationSyntax or NamespaceDeclarationSyntax).ToList();
        var insertions = new List<Insertion>();
        foreach (var declaration in declarations)
        {
            insertions.Add(new Insertion(declaration.SpanStart, "}"));
            insertions.Add(new Insertion(declaration.Span.End, $"partial class {RpadKindTransform.ProgramWrapperClassName}{{"));
        }
        return new ProgramBodyProjection(code, insertions);
    }

    private static bool ContainsExtensionClass(MemberDeclarationSyntax declaration) => declaration switch
    {
        ClassDeclarationSyntax type => IsExtensionClass(type),
        NamespaceDeclarationSyntax ns => ns.Members.Any(ContainsExtensionClass),
        _ => false,
    };

    private static bool IsExtensionClass(ClassDeclarationSyntax declaration) =>
        declaration.Modifiers.Any(SyntaxKind.StaticKeyword)
        && (declaration.Members.OfType<MethodDeclarationSyntax>().Any(m =>
            m.ParameterList.Parameters.FirstOrDefault()?.Modifiers.Any(SyntaxKind.ThisKeyword) == true)
            || declaration.Members.OfType<ExtensionBlockDeclarationSyntax>().Any());

    public int ToProjected(int offset)
    {
        var added = 0;
        foreach (var insertion in _insertions)
        {
            if (insertion.Offset > offset) break;
            added += insertion.Text.Length;
        }
        return offset + added;
    }

    // Synthetic characters have no source position. End-of-source remains a valid caret position.
    public int ToOriginal(int offset)
    {
        var added = 0;
        foreach (var insertion in _insertions)
        {
            var start = insertion.Offset + added;
            if (offset < start) break;
            if (offset < start + insertion.Text.Length) return -1;
            added += insertion.Text.Length;
        }
        return offset - added;
    }

    public TextSpan? ToOriginal(TextSpan span)
    {
        var start = ToOriginal(span.Start);
        var end = span.Length == 0 ? start : ToOriginal(span.End - 1) + 1;
        return start < 0 || end < start || end > Original.Length ? null : TextSpan.FromBounds(start, end);
    }

    /// <summary>Translate a code action without ever writing generated class boundaries into a script.
    /// Actions rewriting those boundaries cannot safely be applied and are refused.</summary>
    public string? RestoreEdit(string changed, string prefix, string suffix, CSharpParseOptions options)
    {
        if (!HasChanges) return changed;
        var before = CSharpSyntaxTree.ParseText(prefix + Text + suffix, options);
        var after = CSharpSyntaxTree.ParseText(prefix + changed + suffix, options);
        var edits = new List<TextChange>();
        foreach (var change in after.GetChanges(before))
        {
            var projectedStart = change.Span.Start - prefix.Length;
            var projectedEnd = change.Span.End - prefix.Length;
            if (projectedStart < 0 || projectedEnd > Text.Length) return null;
            var added = 0;
            var generatedWhitespace = false;
            foreach (var insertion in _insertions)
            {
                var start = insertion.Offset + added;
                var end = start + insertion.Text.Length;
                if (projectedStart >= start && projectedEnd <= end)
                {
                    // A provider may format the generated partial-class header or put it on its
                    // own line. Whitespace there is not part of the user's edit or document.
                    var oldText = Text[projectedStart..projectedEnd];
                    if (oldText.All(char.IsWhiteSpace) && (change.NewText ?? "").All(char.IsWhiteSpace))
                    {
                        generatedWhitespace = true;
                        break;
                    }
                }
                if (projectedStart < end && projectedEnd > start) return null;
                added += insertion.Text.Length;
            }
            if (generatedWhitespace) continue;
            var startOffset = ToOriginal(projectedStart);
            var endOffset = projectedEnd == projectedStart ? startOffset : ToOriginal(projectedEnd - 1) + 1;
            if (startOffset < 0 || endOffset < startOffset) return null;
            edits.Add(new TextChange(TextSpan.FromBounds(startOffset, endOffset), change.NewText ?? ""));
        }
        return SourceText.From(Original).WithChanges(edits).ToString();
    }
}
