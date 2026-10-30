namespace Invoklet.Services;

/// <summary>
/// The pauses of the editor of a tab that wait for the user to stop (typing, moving the pointer, moving the caret) before work
/// starts. The defaults are what the user feels; a test passes <see cref="TimeSpan.Zero"/> so it does not wait for them.
/// </summary>
public sealed record EditorTimings
{
    public static EditorTimings Default { get; } = new();

    /// <summary>Before a Quick Info request after the pointer stops: long enough that passing over a symbol does not spam requests.</summary>
    public TimeSpan QuickInfoHoverDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Before the refactorings at the caret are asked for: the caret moves far more often than the pointer stops
    /// (every arrow key, every click), and refactorings cannot share the debounce of the diagnostics.
    /// </summary>
    public TimeSpan RefactoringDebounce { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>How long a value tip stays when the pointer leaves it before it closes.</summary>
    public TimeSpan DebugTooltipCloseGrace { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Before the Markdown preview follows the text: long enough that typing does not re-render on every key.</summary>
    public TimeSpan PreviewUpdateDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Coalesces a burst of changes (metadata, SDK, schemas) before the context of IntelliSense is rebuilt.</summary>
    public TimeSpan ContextRefreshCoalesce { get; init; } = TimeSpan.FromMilliseconds(50);
}
