using Invoklet.Contracts.IntelliSense;

namespace Invoklet.Services;

/// <summary>What the formatting of an editor needs from the editor: its text, the caret and selection, a way to tell the text has changed, and
/// a way to change it as one edit.</summary>
internal interface IFormattingTarget
{
    string Text { get; }

    int CaretOffset { get; }

    /// <summary>The selection; a zero length at the caret when nothing is selected.</summary>
    (int Start, int Length) Selection { get; }

    /// <summary>A mark of the text as it is now, for <see cref="IsUnchangedSince"/>.</summary>
    object CaptureVersion();

    bool IsUnchangedSince(object version);

    /// <summary>Applies the edits, which do not overlap, as one change that one undo takes back.</summary>
    void Apply(IReadOnlyList<TextEdit> edits);
}

/// <summary>
/// Format Selection and formatting as the user types (after a ';' or a '}'): asks the language service what to change and applies it to the
/// editor as edits, so the caret and what the edits do not touch stay as they were. An answer that arrives after the text changed is dropped,
/// and a newer character typed supersedes the one before it.
/// </summary>
internal sealed class EditorFormatting(Func<IIntelliSenseService?> service, IFormattingTarget target, Func<bool> formatOnType) : IDisposable
{
    private readonly LatestRequest _onType = new();

    /// <summary>Formats the lines of the selection, or the line of the caret when nothing is selected.</summary>
    public async Task FormatSelectionAsync(CancellationToken ct = default)
    {
        if (service() is not { } languageService) return;
        var code = target.Text;
        var version = target.CaptureVersion();
        var (start, length) = target.Selection;

        var edits = await languageService.FormatRangeAsync(code, start, length, ct);
        if (edits is { Count: > 0 } && target.IsUnchangedSince(version)) target.Apply(edits);
    }

    /// <summary>Called after the user typed <paramref name="typed"/>; does nothing for a character that is not formatted after, or when
    /// formatting as you type is off.</summary>
    public async Task FormatOnTypeAsync(char typed)
    {
        if (typed is not (';' or '}') || !formatOnType() || service() is not { } languageService) return;

        var generation = _onType.Invalidate();
        var ct = _onType.NewToken();
        var code = target.Text;
        var caret = target.CaretOffset;
        var version = target.CaptureVersion();
        try
        {
            var edits = await languageService.FormatOnTypeAsync(code, caret, typed, ct);
            if (edits is { Count: > 0 } && _onType.IsCurrent(generation) && target.IsUnchangedSince(version)) target.Apply(edits);
        }
        catch (OperationCanceledException)
        {
            // A newer character was typed.
        }
    }

    public void Dispose() => _onType.Dispose();
}
