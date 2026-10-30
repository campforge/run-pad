using Invoklet.Services.Recovery;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services.Recovery;

public sealed class SessionRecoveryCoordinatorTests
{
    [Fact]
    public async Task LoadAsync_UsesLoaderAndPreservesStoreMetadata()
    {
        var snapshot = new RecoverySnapshot { Revision = 4, Tabs = [] };
        var loadedTabs = new RecoveryTab[] { new() { EditorText = "loaded" } };
        var store = new Mock<IRecoveryStore>();
        store.SetupGet(x => x.Imports).Returns([]);
        store.SetupGet(x => x.Warning).Returns("warning");
        store.Setup(x => x.LoadAsync()).ReturnsAsync(snapshot);
        var loader = new Mock<ISessionSnapshotLoader>();
        loader.Setup(x => x.LoadAsync(snapshot.Tabs)).ReturnsAsync(loadedTabs);
        var coordinator = new SessionRecoveryCoordinator(store.Object, loader.Object);

        var result = await coordinator.LoadAsync();

        Assert.Equal(snapshot.Revision, result.Snapshot!.Revision);
        Assert.Equal(loadedTabs, result.Snapshot!.Tabs);
        Assert.Equal("warning", result.Warning);
    }

    [Fact]
    public async Task CommitAsync_ForwardsSnapshotWithoutChangingIt()
    {
        var snapshot = new RecoverySnapshot { Revision = 7, Tabs = [] };
        var store = new Mock<IRecoveryStore>();
        var coordinator = new SessionRecoveryCoordinator(store.Object, Mock.Of<ISessionSnapshotLoader>());

        await coordinator.CommitAsync(snapshot);

        store.Verify(x => x.CommitAsync(snapshot), Times.Once);
    }
}
