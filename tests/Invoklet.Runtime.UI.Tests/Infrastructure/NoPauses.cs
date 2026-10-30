using Invoklet.Services;

namespace Invoklet.Runtime.UI.Tests.Infrastructure;

// The editor of a tab waits for the user to stop before it works (hover, refactorings, preview). A test that is not about
// the wait itself gives these timings, so it does not sit through them.
internal static class NoPauses
{
    public static EditorTimings Timings => new()
    {
        QuickInfoHoverDelay = TimeSpan.Zero,
        RefactoringDebounce = TimeSpan.Zero,
        DebugTooltipCloseGrace = TimeSpan.Zero,
        PreviewUpdateDelay = TimeSpan.Zero,
        ContextRefreshCoalesce = TimeSpan.Zero,
    };
}
