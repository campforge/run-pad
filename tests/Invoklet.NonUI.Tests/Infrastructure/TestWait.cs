namespace Invoklet.NonUI.Tests;

// Polling with a deadline on the clock instead of a count of rounds: a count budgets a couple of seconds, which a loaded
// machine (the whole suite at once) regularly exceeds, and a deadline costs nothing when the condition is met at once. It
// does not throw on timeout: the caller's own assertion right after says what was missing.
internal static class TestWait
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task WhileAsync(Func<bool> stillWaiting, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (stillWaiting() && DateTime.UtcNow < deadline)
            await Task.Delay(20, cancellationToken);
    }
}
