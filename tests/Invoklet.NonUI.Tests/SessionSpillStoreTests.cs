using Invoklet.Services.Recovery;
using Invoklet.UserControls.Models;
using Xunit;

namespace Invoklet.NonUI.Tests;

// Released tab content waits in the spill folder of the recovery session, which is what makes it private to
// this instance and lets a later start tell that what an earlier one left behind is stale.
public sealed class SessionSpillStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("Invoklet-spill-tests").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ReleasedContent Content(string text) => new(text, new EditorViewState(4, 1, 2, 7.5), null);

    private static async Task<(DiskRecoveryStore Store, SessionSpillStore Spill)> OpenAsync(string root)
    {
        var store = new DiskRecoveryStore(root);
        await store.LoadAsync();
        return (store, new SessionSpillStore(() => store.SessionFolder));
    }

    [Fact]
    public async Task WhatIsWritten_Should_BeReadBack()
    {
        var (store, spill) = await OpenAsync(_root);
        using var _ = store;
        var tab = Guid.NewGuid();

        Assert.True(spill.TryWrite(tab, Content("var a = 1;\nЖ")));

        var read = spill.TryRead(tab)!;
        Assert.Equal("var a = 1;\nЖ", read.Text);
        Assert.Equal(new EditorViewState(4, 1, 2, 7.5), read.Editor);
        Assert.Null(read.Output);
        Assert.StartsWith(store.SessionFolder!, Directory.GetFiles(store.SessionFolder!, "*.json", SearchOption.AllDirectories).Single());
    }

    [Fact]
    public void BeforeTheSessionIsOpen_Should_NothingBeWrittenOrRead()
    {
        var spill = new SessionSpillStore(() => null);

        Assert.False(spill.TryWrite(Guid.NewGuid(), Content("text")));
        Assert.Null(spill.TryRead(Guid.NewGuid()));
    }

    [Fact]
    public async Task ADeletedTab_Should_NotBeReadBack_AndDeleteAllShould_RemoveTheRest()
    {
        var (store, spill) = await OpenAsync(_root);
        using var _ = store;
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        spill.TryWrite(first, Content("one"));
        spill.TryWrite(second, Content("two"));

        spill.Delete(first);
        Assert.Null(spill.TryRead(first));
        Assert.NotNull(spill.TryRead(second));

        spill.DeleteAll();
        Assert.Null(spill.TryRead(second));
        Assert.False(Directory.Exists(Path.Combine(store.SessionFolder!, "spill")));
    }

    [Fact]
    public async Task AnUnreadableFile_Should_ReadAsNothing_NotThrow()
    {
        var (store, spill) = await OpenAsync(_root);
        using var _ = store;
        var tab = Guid.NewGuid();
        spill.TryWrite(tab, Content("text"));
        File.WriteAllText(Directory.GetFiles(store.SessionFolder!, "*.json", SearchOption.AllDirectories).Single(), "{ not json");

        Assert.Null(spill.TryRead(tab));
    }

    [Fact]
    public async Task TheSpillOfAnInstanceThatDiedWithoutCleaningUp_Should_BeRemovedWhenTheNextOneStarts()
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
    public async Task TheSpillOfAnInstanceThatIsStillRunning_Should_BeLeftAlone()
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
}
