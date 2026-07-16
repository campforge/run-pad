using AvaloniaEdit.CodeCompletion;

namespace DotPad.Avalonia.CodeTextEditor;

public sealed class CompletionRequestedEventArgs(
    char trigger, int caretOffset, bool isExplicitInvoke = false, CancellationToken cancellationToken = default)
    : EventArgs
{
    public char Trigger { get; } = trigger;
    public int CaretOffset { get; } = caretOffset;

    /// <summary>True when requested via an explicit invoke gesture (Ctrl+Space) rather than a
    /// trigger character being typed (e.g. '.').</summary>
    public bool IsExplicitInvoke { get; } = isExplicitInvoke;

    /// <summary>Cancelled if a newer completion request supersedes this one before it resolves (fast
    /// typing past the first request — see CodeTextEditor.ShowCompletion, ANALYSIS.md #57). A
    /// provider that threads this through to its own async work (e.g. Roslyn's SemanticModel calls)
    /// gets real cancellation instead of just having its result discarded.</summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>Set by the provider to tell <see cref="CodeTextEditor.ShowCompletion"/> not to fall
    /// back to offering snippets either — e.g. because this position is inside a comment or string
    /// literal, where nothing (language completions or snippets) makes sense. Without this,
    /// snippets were added unconditionally on every explicit/auto-invoke regardless of what the
    /// language completions came back as, since CodeTextEditor has no way to tell "genuinely nothing
    /// here" apart from "not implemented"/"nothing matched" (both leave Items empty too) — only the
    /// provider (which called the language service) knows which case it is.</summary>
    public bool SuppressSnippets { get; set; }

    /// <summary>Set by the provider to tell <see cref="CodeTextEditor.ShowCompletion"/> to show
    /// overloads instead of a completion list — e.g. because the caret sits in a fresh, not-yet-
    /// typed call argument (TODO.md #65), where an explicit Ctrl+Space is really asking for
    /// parameter info rather than a completion list. Only meaningful when <see
    /// cref="IsExplicitInvoke"/> is true; ShowCompletion ignores <see cref="Items"/> entirely when
    /// this is set.</summary>
    public bool RedirectToOverloads { get; set; }

    public List<ICompletionData> Items { get; } = [];
}
