namespace Invoklet.NonUI.Tests.Infrastructure;

// Cleanup for RealBuild test classes whose temp folder holds DLLs a just-exited script process loaded.
// Windows CI (release 0.3.0) threw UnauthorizedAccessException deleting them right after a run:
// bin/Invoklet.Runtime.dll in ScriptCompilerTests, Feed.Greeter.dll in ScriptPackageRestoreTests. What
// held the file in CI wasn't verified (RunAsync does wait for the script process to exit). Retries
// for a bounded time only (~2s); a lock that outlives it still fails the test with the original
// exception.
internal static class TempDirectoryCleanup
{
    private const int MaxRetries = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    public static async ValueTask DeleteAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < MaxRetries)
            {
                await Task.Delay(RetryDelay);
            }
        }
    }
}
