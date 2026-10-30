using Invoklet.Contracts;
using Invoklet.NonUI.Tests.App.Services.Fixtures;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class PackageSearchRunnerTests
{
    private static readonly PackageSearchPage OnePage = new([], HasMore: true);

    [Fact]
    public async Task RunAsync_CurrentSearch_ReturnsThePageAfterTellingItStarted()
    {
        // Arrange
        var fixture = new PackageSearchRunnerFixture();
        fixture.Search.Setup(s => s.SearchAsync("json", PackageSearchRunnerFixture.Options, It.IsAny<CancellationToken>())).ReturnsAsync(OnePage);
        var sut = fixture.CreateSut();
        var started = 0;

        // Act
        var outcome = await sut.RunAsync("json", PackageSearchRunnerFixture.Options, true, () => true, () => started++, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, started);
        Assert.Same(OnePage, outcome!.Page);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public async Task RunAsync_SupersededBeforeTheSearch_NeverCallsTheService()
    {
        // Arrange
        var fixture = new PackageSearchRunnerFixture();
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.RunAsync("json", PackageSearchRunnerFixture.Options, true, () => false, () => { }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(outcome);
        fixture.Search.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_SupersededWhileSearching_ReturnsNothingEvenThoughTheServiceAnswered()
    {
        // Arrange
        var fixture = new PackageSearchRunnerFixture();
        fixture.Search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(OnePage);
        var sut = fixture.CreateSut();
        var wanted = true;

        // Act: the search is dropped from the start callback on, as a newer query would
        var outcome = await sut.RunAsync("json", PackageSearchRunnerFixture.Options, true, () => wanted, () => wanted = false, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(outcome);
    }

    [Fact]
    public async Task RunAsync_TheServiceFails_ReturnsAnEmptyPageAndTheMessage()
    {
        // Arrange
        var fixture = new PackageSearchRunnerFixture();
        fixture.Search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no network"));
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.RunAsync("json", PackageSearchRunnerFixture.Options, true, () => true, () => { }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(outcome!.Page.Results);
        Assert.False(outcome.Page.HasMore);
        Assert.Equal("Search failed: no network", outcome.Error);
    }

    [Fact]
    public async Task RunAsync_CancelledDuringTheDebounce_ReturnsNothingAndNeverSearches()
    {
        // Arrange
        var fixture = new PackageSearchRunnerFixture { Debounce = TimeSpan.FromMinutes(5) };
        var sut = fixture.CreateSut();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        var running = sut.RunAsync("json", PackageSearchRunnerFixture.Options, false, () => true, () => { }, cts.Token);
        await cts.CancelAsync();
        var outcome = await running;

        // Assert
        Assert.Null(outcome);
        fixture.Search.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ImmediateSearch_SkipsTheDebounce()
    {
        // Arrange
        var fixture = new PackageSearchRunnerFixture { Debounce = TimeSpan.FromMinutes(5) };
        fixture.Search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(OnePage);
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.RunAsync("json", PackageSearchRunnerFixture.Options, true, () => true, () => { }, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(OnePage, outcome!.Page);
    }

    [Fact]
    public async Task RunAsync_SupersededWhileTheServiceIsStillWorking_DropsThePageItLaterReturns()
    {
        // Arrange
        var fixture = new PackageSearchRunnerFixture();
        var inService = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<PackageSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                inService.SetResult();
                return answer.Task;
            });
        var sut = fixture.CreateSut();
        var wanted = true;
        var running = sut.RunAsync("json", PackageSearchRunnerFixture.Options, true, () => wanted, () => { }, TestContext.Current.CancellationToken);
        await inService.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Act: a newer query takes over while the service is still working, and the old one answers late
        wanted = false;
        answer.SetResult(OnePage);
        var outcome = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(outcome);
    }
}
