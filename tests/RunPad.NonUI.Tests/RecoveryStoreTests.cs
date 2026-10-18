using System.Text.Json;
using RunPad.Services.Recovery;
using Xunit;

namespace RunPad.NonUI.Tests;

public sealed class RecoveryStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("runpad-recovery-tests").FullName;
    private static RecoverySnapshot Snapshot(long revision, string text = "draft\r\nЖ\n${0}")
    {
        var tab = new RecoveryTab { EditorText = text, IsDirty = true, BaselineKnown = true, DiskHash = [1, 2, 3] };
        return new RecoverySnapshot { Revision = revision, Tabs = [tab], ActiveTab = tab.Id };
    }

    [Fact]
    public async Task CommitAndReload_Should_PreserveExactTextAndBaseline()
    {
        var snapshot = Snapshot(1);
        using (var store = new DiskRecoveryStore(_root)) await store.CommitAsync(snapshot);
        using var next = new DiskRecoveryStore(_root);
        var restored = (await next.LoadAsync())!;
        Assert.Equal(snapshot.Tabs[0].EditorText, restored.Tabs[0].EditorText);
        Assert.Equal(snapshot.Tabs[0].DiskHash, restored.Tabs[0].DiskHash);
        Assert.Equal(snapshot.ActiveTab, restored.ActiveTab);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("previous")]
    [InlineData("current.json")]
    [InlineData("committed")]
    public async Task InterruptedCommit_Should_KeepACompleteGeneration(string stage)
    {
        using (var first = new DiskRecoveryStore(_root)) await first.CommitAsync(Snapshot(1, "original"));
        using (var failing = new DiskRecoveryStore(_root, step => { if (step == stage) throw new IOException("injected"); }))
            await Assert.ThrowsAsync<IOException>(() => failing.CommitAsync(Snapshot(2, "next")));
        using var reopened = new DiskRecoveryStore(_root);
        var restored = (await reopened.LoadAsync())!;
        Assert.Equal(stage == "committed" ? "next" : "original", restored.Tabs[0].EditorText);
    }

    [Fact]
    public async Task CorruptPointer_Should_FallBackToPreviousGeneration()
    {
        string folder;
        using (var store = new DiskRecoveryStore(_root))
        {
            await store.CommitAsync(Snapshot(1, "one"));
            await store.CommitAsync(Snapshot(2, "two"));
            folder = Assert.Single(Directory.GetDirectories(_root));
        }
        File.WriteAllText(Path.Combine(folder, "current.json"), "{");
        using var next = new DiskRecoveryStore(_root);
        Assert.Equal("one", (await next.LoadAsync())!.Tabs[0].EditorText);
    }

    [Fact]
    public async Task LiveOwner_Should_NotBeClaimedOrOverwritten()
    {
        using var one = new DiskRecoveryStore(_root);
        await one.CommitAsync(Snapshot(1, "first instance"));
        using var two = new DiskRecoveryStore(_root);
        Assert.Null(await two.LoadAsync());
        await two.CommitAsync(Snapshot(1, "second instance"));
        Assert.Equal(2, Directory.GetDirectories(_root).Length);
    }

    [Fact]
    public async Task OlderRevision_Should_NotReplaceNewer_AndEmptySessionIsAValidCommit()
    {
        using (var store = new DiskRecoveryStore(_root))
        {
            await store.CommitAsync(Snapshot(2));
            await store.CommitAsync(Snapshot(1, "stale"));
            await store.CommitAsync(new RecoverySnapshot { Revision = 3, Tabs = [] });
        }
        using var reopened = new DiskRecoveryStore(_root);
        var snapshot = (await reopened.LoadAsync())!;
        Assert.Equal(3, snapshot.Revision);
        Assert.Empty(snapshot.Tabs);
    }

    [Fact]
    public async Task UnknownVersion_Should_KeepDataAndRefuseToOverwriteIt()
    {
        using (var store = new DiskRecoveryStore(_root)) await store.CommitAsync(Snapshot(1));
        var pointer = Path.Combine(Assert.Single(Directory.GetDirectories(_root)), "current.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = 99, Current = "unknown", Previous = (string?)null });
        File.WriteAllBytes(pointer, bytes);
        using var next = new DiskRecoveryStore(_root);
        await Assert.ThrowsAsync<NotSupportedException>(() => next.LoadAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => next.CommitAsync(Snapshot(2)));
        Assert.Equal(bytes, File.ReadAllBytes(pointer));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
