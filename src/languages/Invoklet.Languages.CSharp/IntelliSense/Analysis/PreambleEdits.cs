using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace Invoklet.Languages.CSharp.IntelliSense.Analysis;

/// <summary>
/// Turns the edit a code fix made to the whole analysis document back into an edit of the user's code: what falls in the preamble is
/// either a plain <c>using</c> line, lifted out as a using to add to the script, or something unsafe to reconstruct, which discards
/// the fix; what falls in the user's code is moved back by the length of the preamble.
/// </summary>
internal sealed class PreambleEdits(ILogger logger)
{
    private readonly ILogger _logger = logger;

    private static readonly Regex _usingInsertionRegex = new(@"^\s*using\s+([\w.]+)\s*;\s*$", RegexOptions.Compiled);

    // Splits a set of TextChanges expressed against the synthetic `_preamble + code` document into
    // "usings to add" (lifted out of the preamble) and "the rest" (shifted back onto the user's
    // code). See the class doc comment for why this can't be a plain substring cut.
    //
    // The "rest" used to be reconstructed as everything from preambleLength to
    // originalFullText.Length — correct only as long as nothing follows the user's own code in the
    // synced document. CSharpIntelliSenseService now appends a one-character TrailingPad after it
    // (so a query position sitting at the very end of the user's code no longer coincides with the
    // document's true end — see HeaderLength's doc comment for the underlying Roslyn quirk this
    // works around), so the end of "the user's code" has to be the caller-supplied, actually-known
    // userCodeLength instead of however long the live document happens to be. A fix that removes/
    // replaces the LAST token of the user's code (e.g. "remove unused variable" on the final
    // statement) removes that token's full span including trailing trivia — which now extends into
    // TrailingPad, since a synthetic space right after the last real token attaches to it as trailing
    // trivia the same as any other trailing whitespace. Each change's span is clamped to
    // preambleLength + userCodeLength for exactly this reason: the clamped-off tail is guaranteed to
    // be nothing but synthetic wrapper text — TrailingPad, and for MdRunKind.Program also the
    // class-closing suffix mirrored by CSharpIntelliSenseService.KindSuffix — never
    // meaningful user content, so silently dropping it from what gets deleted/replaced is safe —
    // NewText itself is untouched, only how much of the (synthetic-only) tail end of the input span
    // gets consumed is shortened.
    internal (string NewCode, IReadOnlyList<string> UsingsToAdd, IReadOnlyList<TextSpan> ChangedSpans)? ApplyRelocatingPreambleEdits(
        List<TextChange> changes, SourceText originalFullText, int preambleLength, int userCodeLength)
    {
        var usingsToAdd = new List<string>();
        var userCodeChanges = new List<TextChange>();
        var userCodeEnd = preambleLength + userCodeLength;

        if (changes.Count > 0 && changes.All(c => string.IsNullOrEmpty(c.NewText))
            && changes.Any(c => c.Span.Start < preambleLength))
            return ApplyLineDeletionsAcrossPreamble(changes, originalFullText, preambleLength, userCodeLength);

        var preambleChanges = new List<TextChange>();
        foreach (var change in changes.OrderBy(c => c.Span.Start))
        {
            if (change.Span.Start >= preambleLength)
            {
                var clampedLength = Math.Max(0, Math.Min(change.Span.End, userCodeEnd) - change.Span.Start);
                userCodeChanges.Add(new TextChange(
                    new TextSpan(change.Span.Start - preambleLength, clampedLength), change.NewText ?? ""));
                continue;
            }

            // Touches the hidden preamble. Only `using X;` insertions there are recoverable — a change
            // that straddles into the user's code can't be split safely, so bail out entirely.
            if (change.Span.End > preambleLength)
            {
                _logger.LogDebug(
                    "Discarding fix: preamble-touching change [{Start}..{End}) straddles preambleLength={PreambleLength}",
                    change.Span.Start, change.Span.End, preambleLength);
                return null;
            }
            preambleChanges.Add(change);
        }

        if (preambleChanges.Count > 0)
        {
            var preambleText = originalFullText.GetSubText(new TextSpan(0, preambleLength)).ToString();
            var inserted = UsingsInsertedIntoPreamble(preambleText, preambleChanges);
            if (inserted is null)
            {
                _logger.LogDebug(
                    "Discarding fix: preamble-touching changes {Changes} aren't only `using X;` insertions",
                    string.Join(" | ", preambleChanges.Select(c => $"[{c.Span.Start}..{c.Span.End}) -> \"{RoslynCodeFixEngine.Truncate(c.NewText)}\"")));
                return null;
            }
            usingsToAdd.AddRange(inserted);
        }

        var userText = originalFullText.GetSubText(new TextSpan(preambleLength, userCodeLength));
        return (userText.WithChanges(userCodeChanges).ToString(), usingsToAdd, ChangedSpans(userCodeChanges));
    }

    // Where each change ended up in the changed text: its replacement's span, shifted by what the
    // earlier changes added or removed. A deletion leaves an empty span where its text was.
    private static List<TextSpan> ChangedSpans(IEnumerable<TextChange> changes)
    {
        var spans = new List<TextSpan>();
        var shift = 0;
        foreach (var change in changes.OrderBy(c => c.Span.Start))
        {
            var length = change.NewText?.Length ?? 0;
            spans.Add(new TextSpan(change.Span.Start + shift, length));
            shift += length - change.Span.Length;
        }
        return spans;
    }

    // The namespaces a fix adds to the hidden preamble, or null if its preamble changes do anything
    // other than insert whole `using X;` lines (replace or remove an existing line, insert other text).
    // Usually such a fix arrives as one zero-length insertion of "using X;\n" at a line start, but the
    // diff Roslyn computes depends on its version (and with it on the installed SDK): a using sorted
    // into the middle of the preamble can instead come back as a minimal character diff that rewrites
    // the following lines ("IO" -> "Diagnostics", "Linq" -> "IO", ...) while still meaning "one line
    // inserted". So the changes are applied to the preamble text and the result is compared line by
    // line: every original line must still be there, in order, and every extra line must be a using.
    internal static IReadOnlyList<string>? UsingsInsertedIntoPreamble(string preambleText, IEnumerable<TextChange> changes)
    {
        var oldLines = SplitLinesKeepingTerminators(preambleText);
        var newLines = SplitLinesKeepingTerminators(SourceText.From(preambleText).WithChanges(changes).ToString());
        var usings = new List<string>();
        var next = 0;
        foreach (var line in newLines)
        {
            if (next < oldLines.Count && line == oldLines[next])
            {
                next++;
                continue;
            }

            var match = _usingInsertionRegex.Match(line);
            if (!match.Success)
                return null;
            usings.Add(match.Groups[1].Value);
        }

        return next == oldLines.Count ? usings : null;
    }

    // Deletion-only fixes that also touch the preamble — "Remove unnecessary usings" removes the
    // preamble's own unused default usings together with the user's (live probe: one TextChange,
    // [20..207) -> "" with preambleLength 184). Such a minimal diff can't be split at preambleLength:
    // its boundaries are shifted by whatever text the deleted and surviving lines share ("using "),
    // so the part past preambleLength isn't the user's deleted line at all. Instead, the fix's result
    // is matched line by line against the original: every surviving line must still be there in
    // order (a pure line deletion), deleted preamble lines are ignored — the preamble is regenerated
    // on every sync and leaving an unneeded using there can't change what the user's code means —
    // and only deleted user-code lines are applied. Anything that isn't whole-line deletion bails out.
    internal (string NewCode, IReadOnlyList<string> UsingsToAdd, IReadOnlyList<TextSpan> ChangedSpans)? ApplyLineDeletionsAcrossPreamble(
        List<TextChange> changes, SourceText originalFullText, int preambleLength, int userCodeLength)
    {
        var originalText = originalFullText.ToString();
        var newLines = SplitLinesKeepingTerminators(originalFullText.WithChanges(changes).ToString());
        var userCodeEnd = preambleLength + userCodeLength;
        var userCodeChanges = new List<TextChange>();

        var next = 0;
        var offset = 0;
        foreach (var line in SplitLinesKeepingTerminators(originalText))
        {
            if (next < newLines.Count && newLines[next] == line)
            {
                next++;
            }
            else if (offset >= preambleLength && offset < userCodeEnd)
            {
                // The user's last line can run on into synthetic trailing text (TrailingPad/KindSuffix)
                // — only its user-code part is deleted, same clamping rule as the regular path.
                var end = Math.Min(offset + line.Length, userCodeEnd);
                userCodeChanges.Add(new TextChange(new TextSpan(offset - preambleLength, end - offset), ""));
            }

            offset += line.Length;
        }

        if (next != newLines.Count)
        {
            _logger.LogDebug("Discarding fix: preamble-touching deletion isn't a whole-line deletion");
            return null;
        }

        var userText = originalFullText.GetSubText(new TextSpan(preambleLength, userCodeLength));
        return (userText.WithChanges(userCodeChanges).ToString(), [], ChangedSpans(userCodeChanges));
    }

    private static List<string> SplitLinesKeepingTerminators(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }
}
