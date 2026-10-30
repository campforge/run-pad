using Invoklet.Services;

namespace Invoklet.NonUI.Tests;

// The SUT has no injected dependencies: the fixture owns the test data (runs that finish when the test says so)
// and creates a new instance for each test.
public sealed class RunLifecycleFixture
{
    /// <summary>A run that ends when <see cref="TaskCompletionSource.SetResult()"/> is called.</summary>
    public TaskCompletionSource NewRun() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Starts a thread of its own, as a host reports its events on one.</summary>
    public static Task RunOnAnotherThread(Action work) => Task.Factory.StartNew(work, TaskCreationOptions.LongRunning);

    internal RunLifecycle CreateSut() => new();
}
