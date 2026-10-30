using System.Runtime.CompilerServices;

namespace Invoklet.IntegrationTests;

// The windows await work started with Task.Run while tests wait on the UI thread, and the pool adds a
// thread a second once its minimum is reached, so each such await cost about a second. The minimum is
// raised once here instead of in each test class.
internal static class TestThreadPoolSetup
{
    [ModuleInitializer]
    internal static void RaiseMinimumThreads() => ThreadPool.SetMinThreads(512, 512);
}
