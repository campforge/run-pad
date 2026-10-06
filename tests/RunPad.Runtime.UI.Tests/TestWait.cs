namespace RunPad.Runtime.UI.Tests;

// Polling with a wall-clock deadline instead of a fixed iteration count. `for (i < 100) Delay(20)`
// budgets only ~2s, which a cold Roslyn workspace or a loaded machine (parallel builds, a debugger)
// regularly exceeds — those tests failed intermittently. A generous deadline costs nothing when the
// condition is met promptly. Like the loops it replaces, it doesn't throw on timeout: the caller's
// own Assert right after reports what was actually missing.
internal static class TestWait
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task WhileAsync(Func<bool> stillWaiting, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (stillWaiting() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }
}
