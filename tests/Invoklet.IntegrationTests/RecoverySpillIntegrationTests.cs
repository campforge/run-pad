using Invoklet.Services.Recovery;
using Invoklet.UserControls.Models;
using Xunit;

namespace Invoklet.IntegrationTests;

// The spill on a real disk, in the session folders of a real DiskRecoveryStore: what a restarted instance finds of
// an earlier one, and what a running one keeps. (Unit tests of SessionSpillStore use a fake for the files.)
public sealed class RecoverySpillIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("Invoklet-spill-tests").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ReleasedContent Content(string text, TabOutputState? output = null)
        => new(text, new EditorViewState(4, 1, 2, 7.5), output);

    private static async Task<(DiskRecoveryStore Store, SessionSpillStore Spill)> OpenAsync(string root)
    {
        var store = new DiskRecoveryStore(root);
        await store.LoadAsync();
        return (store, new SessionSpillStore(store));
    }

    [Fact]
    public async Task WhatIsWritten_OnARealDisk_IsReadBackFromTheSessionFolder()
    {
        var (store, spill) = await OpenAsync(_root);
        using var _ = store;
        var tab = Guid.NewGuid();

        var written = spill.TryWrite(tab, Content("var a = 1;\nЖ"));

        Assert.True(written);
        Assert.Equal("var a = 1;\nЖ", spill.TryRead(tab)!.Text);
        Assert.StartsWith(store.SessionFolder!, Directory.GetFiles(store.SessionFolder!, "*.json", SearchOption.AllDirectories).Single());
    }

    [Fact]
    public async Task TheSpillOfAnInstanceThatDiedWithoutCleaningUp_IsRemovedWhenTheNextOneStarts()
    {
        string stale;
        var tab = Guid.NewGuid();
        {
            var (store, spill) = await OpenAsync(_root);
            spill.TryWrite(tab, Content("old text of someone else's tab"));
            stale = Path.Combine(store.SessionFolder!, "spill");
            store.Dispose(); // as a crash leaves it: the folder and the file stay, the lock goes
        }
        Assert.True(Directory.Exists(stale));

        var (next, nextSpill) = await OpenAsync(_root);
        using var _ = next;

        Assert.False(Directory.Exists(stale));
        Assert.Null(nextSpill.TryRead(tab)); // not even if the tab got the same id
    }

    [Fact]
    public async Task TheSpillOfAnInstanceThatIsStillRunning_IsLeftAlone()
    {
        var (running, runningSpill) = await OpenAsync(_root);
        using var _ = running;
        var tab = Guid.NewGuid();
        runningSpill.TryWrite(tab, Content("mine"));

        var (other, otherSpill) = await OpenAsync(_root);
        using var __ = other;

        Assert.Equal("mine", runningSpill.TryRead(tab)!.Text);
        Assert.NotEqual(running.SessionFolder, other.SessionFolder);
        Assert.Null(otherSpill.TryRead(tab));
    }

    [Fact]
    public async Task TheOutputOfAScriptWithoutAFolderOfItsRuns_LivesOnlyAsLongAsTheInstance()
    {
        // An unsaved script has no build folder to keep its output in, so the output goes to the spill, which
        // is not part of a recovery checkpoint: the text of the draft comes back after a restart, its output does not.
        var tab = Guid.NewGuid();
        {
            var (store, spill) = await OpenAsync(_root);
            spill.TryWrite(tab, Content("draft", new TabOutputState("<div>output</div>", false, new Dictionary<string, string>())));
            Assert.Equal("<div>output</div>", spill.TryRead(tab)!.Output!.Content);
            store.Dispose();
        }

        var (next, nextSpill) = await OpenAsync(_root);
        using var _ = next;

        Assert.Null(nextSpill.TryRead(tab));
    }
}
