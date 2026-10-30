using Invoklet.Services;

namespace Invoklet.NonUI.Tests;

internal sealed class WorkspaceWorkFixture
{
    /// <summary>A tab as the work sees it: when its editor is ready and which saves are going on.</summary>
    public sealed class FakeTab(string name)
    {
        public string Name { get; } = name;

        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? PendingSave { get; set; }

        public Task? PendingSaveAs { get; set; }

        public FakeTab AlreadyReady()
        {
            Ready.SetResult();
            return this;
        }
    }

    public List<FakeTab> Tabs { get; } = [];

    public static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<List<FakeTab>> WaitUntilIdle(WorkspaceWork sut, Func<FakeTab, bool>? include = null)
        => sut.WaitUntilIdleAsync<FakeTab>(() => [.. Tabs], include ?? (_ => true), t => t.Ready.Task, t => [t.PendingSave, t.PendingSaveAs]);

    public WorkspaceWork CreateSut(TimeSpan? readyTimeout = null) => new(readyTimeout);
}
