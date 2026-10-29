using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Invoklet.Languages.CSharp;

/// <summary>
/// Lets member-style programs declare extension methods, as LINQPad does: the script's types stay
/// nested in the generated class, except that once any top-level static class declares extension
/// methods (which C# allows only in a non-nested static class), every top-level static class and
/// namespace is moved out of it. Moved classes see the nested types and the script's members
/// through the prefix's <c>using static</c>, as far as their accessibility allows: a type a moved
/// class uses, such as an extension's receiver, must be internal or public, as in LINQPad.
/// Also hoists the script's own leading <c>extern alias</c>/<c>using</c> directives, which are only
/// valid before the generated wrapper class, never inside it (CS1529) — <see cref="LeadingDirectives"/>
/// is spliced into the prefix instead (see <see cref="RpadKindTransform.ProgramPrefixFor"/>), and the
/// source lines they came from are replaced by whitespace here so line numbers stay put. Only
/// synthetic braces, partial-class headers and that whitespace are introduced; no original character
/// moves or is removed, and no line is inserted or deleted. The same projection is used by the
/// compiler and editor.
/// </summary>
internal sealed class ProgramBodyProjection
{
    private sealed record Insertion(int Offset, string Text);
    private readonly IReadOnlyList<Insertion> _insertions;
    public string Original { get; }

    /// <summary>The declarations compiled outside the generated class, in <see cref="Original"/>.</summary>
    public IReadOnlyList<TextSpan> Moved { get; }
    public string Text { get; }
    public bool HasChanges => _insertions.Count != 0 || LeadingDirectives.Length != 0;

    /// <summary>The script's own leading extern-alias/using directives, verbatim, or "" if none —
    /// blanked out of <see cref="Text"/> and meant to be spliced before the wrapper class instead.</summary>
    public string LeadingDirectives { get; }

    /// <summary>Where <see cref="LeadingDirectives"/> starts in <see cref="Original"/>.</summary>
    public int LeadingDirectivesStart { get; }

    /// <summary>Whether <see cref="LeadingDirectives"/> ends with a hoisted <c>#endif</c> (detected
    /// structurally, by matching the span its <see cref="ConditionalGroups"/> entry ends at — not by
    /// matching the literal text "#endif", since a valid directive can be followed by a trailing
    /// comment or spaces on the same line). A directive must be the only thing on its line, so callers
    /// that glue more text after <see cref="LeadingDirectives"/> need to start a new line instead.</summary>
    public bool LeadingDirectivesEndWithConditionalBlock { get; }

    private ProgramBodyProjection(string code, string blanked, IReadOnlyList<Insertion> insertions,
        IReadOnlyList<TextSpan> moved, string leadingDirectives, int leadingDirectivesStart,
        bool leadingDirectivesEndWithConditionalBlock)
    {
        Original = code;
        _insertions = insertions;
        Moved = moved;
        LeadingDirectives = leadingDirectives;
        LeadingDirectivesStart = leadingDirectivesStart;
        LeadingDirectivesEndWithConditionalBlock = leadingDirectivesEndWithConditionalBlock;
        var builder = new StringBuilder();
        var offset = 0;
        foreach (var insertion in insertions)
        {
            builder.Append(blanked, offset, insertion.Offset - offset).Append(insertion.Text);
            offset = insertion.Offset;
        }
        Text = builder.Append(blanked, offset, blanked.Length - offset).ToString();
    }

    public static ProgramBodyProjection Identity(string code) => new(code, code, [], [], "", 0, false);

    public static ProgramBodyProjection Create(string code, CSharpParseOptions options)
    {
        var root = CSharpSyntaxTree.ParseText(code, options).GetCompilationUnitRoot();
        if (!root.Members.OfType<GlobalStatementSyntax>().Any()) return Identity(code);

        // Extern aliases always precede usings, and both always precede every other declaration —
        // the only place they can legally be in a compilation unit — so this is exactly the
        // directives that would otherwise land inside the wrapper class and fail to compile (CS1529).
        var directives = root.Externs.Cast<SyntaxNode>().Concat(root.Usings).ToList();
        var leadingDirectives = "";
        var leadingStart = 0;
        var endsInConditionalBlock = false;
        var blanked = code;
        if (directives.Count > 0)
        {
            var start = directives[0].SpanStart;
            var end = directives[^1].Span.End;
            // A using/extern can sit inside #if/#elif/#else/#endif (e.g. a platform-specific
            // import). The bounds above stop at the directive nodes themselves and exclude the
            // surrounding trivia, so they can split such a block in half: the #if ends up before
            // `start` (it's leading trivia of the first using inside it) while the #endif ends up
            // after `end` (trailing trivia of the last using inside it) — moving one without the
            // other leaves both the prefix and the body unable to compile. Grow the span so every
            // #if/#endif pair straddling it is pulled in whole instead.
            bool grew;
            do
            {
                grew = false;
                foreach (var (groupStart, groupEnd) in ConditionalGroups(root))
                {
                    if (groupStart < end && groupEnd > start && (groupStart < start || groupEnd > end))
                    {
                        start = Math.Min(start, groupStart);
                        end = Math.Max(end, groupEnd);
                        grew = true;
                    }
                }
            } while (grew);
            // Structural, not textual: a hoisted #endif's trivia span includes any trailing comment
            // or spaces on its line, so the span's end lands exactly on a conditional group's end
            // whether or not the directive text itself ends in "#endif".
            endsInConditionalBlock = ConditionalGroups(root).Any(group => group.End == end);
            var span = TextSpan.FromBounds(start, end);
            leadingDirectives = code.Substring(span.Start, span.Length);
            leadingStart = span.Start;
            blanked = string.Concat(code.AsSpan(0, span.Start), Blank(leadingDirectives), code.AsSpan(span.End));
        }

        // Static classes move, extension ones or not (as in LINQPad); other types stay nested, where
        // they keep seeing the script's own members. A namespace can't be nested in a class at all.
        // Types nested inside a moved declaration stay where they are: only compilation-unit members move.
        var declarations = root.Members.Any(ContainsExtensionClass)
            ? root.Members.Where(m => m is NamespaceDeclarationSyntax
                || (m is ClassDeclarationSyntax type && type.Modifiers.Any(SyntaxKind.StaticKeyword))).ToList()
            : [];
        var insertions = new List<Insertion>();
        foreach (var declaration in declarations)
        {
            insertions.Add(new Insertion(declaration.SpanStart, "}"));
            insertions.Add(new Insertion(declaration.Span.End, $"partial class {RpadKindTransform.ProgramWrapperClassName}{{"));
        }
        if (insertions.Count == 0 && directives.Count == 0) return Identity(code);
        return new ProgramBodyProjection(code, blanked, insertions, [.. declarations.Select(d => d.Span)], leadingDirectives,
            leadingStart, endsInConditionalBlock);
    }

    // Keeps line/column structure (and so offsets) exactly as in the original: only '\r'/'\n' survive.
    private static string Blank(string text) => string.Concat(text.Select(c => c is '\r' or '\n' ? c : ' '));

    // Every #if...#endif pair in the tree (however nested), as the span from the #if to its
    // matching #endif. #elif/#else branches in between don't change the nesting depth, so they
    // need no special handling: whichever #if opened the block is still the one its #endif closes.
    private static IEnumerable<(int Start, int End)> ConditionalGroups(CompilationUnitSyntax root)
    {
        var opens = new Stack<int>();
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (!trivia.IsDirective) continue;
            switch (trivia.Kind())
            {
                case SyntaxKind.IfDirectiveTrivia:
                    opens.Push(trivia.SpanStart);
                    break;
                case SyntaxKind.EndIfDirectiveTrivia when opens.Count > 0:
                    yield return (opens.Pop(), trivia.Span.End);
                    break;
            }
        }
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
    /// Actions rewriting those boundaries cannot safely be applied and are refused. Also returns where
    /// the action's text ended up in the script (see <paramref name="changedSpans"/>, the same for
    /// the projected text), for the editor's re-indentation.</summary>
    public (string Code, IReadOnlyList<TextSpan> ChangedSpans)? RestoreEdit(string changed, IReadOnlyList<TextSpan> changedSpans,
        string prefix, string suffix, CSharpParseOptions options)
    {
        if (!HasChanges) return (changed, changedSpans);
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
        var spans = new List<TextSpan>(edits.Count);
        var shift = 0;
        foreach (var edit in edits.OrderBy(e => e.Span.Start))
        {
            var length = edit.NewText?.Length ?? 0;
            spans.Add(new TextSpan(edit.Span.Start + shift, length));
            shift += length - edit.Span.Length;
        }
        return (SourceText.From(Original).WithChanges(edits).ToString(), spans);
    }
}
