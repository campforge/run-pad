namespace Invoklet.Runtime.UI.Tests;

// Polling with a wall-clock deadline instead of a fixed iteration count. `for (i < 100) Delay(20)`
// budgets only ~2s, which a cold Roslyn workspace or a loaded machine (parallel builds, a debugger)
// regularly exceeds — those tests failed intermittently. A generous deadline costs nothing when the
// condition is met promptly. Like the loops it replaces, it doesn't throw on timeout: the caller's
// own Assert right after reports what was actually missing.
internal static class TestWait
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // Returns once `count` has stayed the same for `quiet`: whatever a just-opened tab started in the
    // background has finished, however slowly a loaded machine ran it.
    public static async Task SettleAsync(Func<int> count, TimeSpan? quiet = null, TimeSpan? timeout = null)
    {
        var window = quiet ?? TimeSpan.FromMilliseconds(700);
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var last = count();
        var since = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline && DateTime.UtcNow - since < window)
        {
            await Task.Delay(20);
            var now = count();
            if (now == last) continue;
            last = now;
            since = DateTime.UtcNow;
        }
    }

    // Runs the check until it stops throwing: an effect the tab applies on a worker thread (the
    // editor's context) shows up shortly after the action that caused it. The last failure is rethrown.
    public static async Task EventuallyAsync(Action check, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            try
            {
                check();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
    }

    public static async Task WhileAsync(Func<bool> stillWaiting, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (stillWaiting() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }
}
