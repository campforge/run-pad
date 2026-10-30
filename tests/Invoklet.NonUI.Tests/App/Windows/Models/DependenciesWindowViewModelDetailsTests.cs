using System.Reactive.Linq;
using Invoklet.Contracts;
using Invoklet.Format.MdRun;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Windows.Models;

// The package card's dependencies, license and project page: loaded for the selected version off the
// UI thread, shown only when a source supplies them, and never in the way of adding the package.
public class DependenciesWindowViewModelDetailsTests
{
    private static readonly PackageSearchResult Json = new("Newtonsoft.Json", "13.0.3", "JSON", 100);

    private static PackageDetails Full() => new(
        [
            new PackageDependencyGroupInfo("net8.0", [new PackageDependencyInfo("System.Memory", ">= 4.5.5")]),
            new PackageDependencyGroupInfo("netstandard2.0", [new PackageDependencyInfo("A", ">= 1.0.0"), new PackageDependencyInfo("B", "")]),
        ],
        "MIT", "https://licenses.nuget.org/MIT", "https://www.newtonsoft.com/json");

    private static (DependenciesWindowViewModel Vm, Mock<IPackageSearchService> Search) CreateVm(
        Func<string, string?, CancellationToken, Task<PackageDetails?>> details)
    {
        var search = new Mock<IPackageSearchService>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageSearchPage([Json], HasMore: false));
        search.Setup(s => s.GetVersionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["13.0.3", "12.0.3"]);
        search.Setup(s => s.GetDetailsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string id, string? version, CancellationToken ct) => details(id, version, ct));
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Returns([]);
        var sources = new Mock<INuGetSourceStore>();
        sources.Setup(s => s.Load()).Returns([new NuGetSource("nuget.org", "https://api.nuget.org/v3/index.json")]);
        var vm = new DependenciesWindowViewModel(search.Object, cache.Object, sources.Object, TimeSpan.Zero);
        vm.SetFrontMatter(new MdRunFrontMatter());
        return (vm, search);
    }

    private static async Task SelectAsync(DependenciesWindowViewModel vm)
    {
        vm.SearchText = "newtonsoft";
        await vm.PendingSearch;
        vm.SelectedOnlinePackage = vm.OnlinePackages.Single();
        await vm.PendingVersions;
        await vm.PendingDetails;
    }

    [Fact]
    public async Task TheCard_Should_ShowDependenciesLicenseAndProjectPage_WhenTheSourceSuppliesThem()
    {
        var (vm, search) = CreateVm((_, _, _) => Task.FromResult<PackageDetails?>(Full()));

        await SelectAsync(vm);

        Assert.Equal(["net8.0", "System.Memory (>= 4.5.5)", "netstandard2.0", "A (>= 1.0.0)", "B"],
            vm.DetailsDependencies.Select(l => l.Text));
        Assert.Equal([true, false, true, false, false], vm.DetailsDependencies.Select(l => l.IsHeader));
        Assert.True(vm.HasDetailsDependencies);
        Assert.False(vm.DetailsHaveNoDependencies);
        Assert.Equal("MIT", vm.DetailsLicense);
        Assert.Equal("https://licenses.nuget.org/MIT", vm.DetailsLicenseUrl);
        Assert.Equal("https://www.newtonsoft.com/json", vm.DetailsProjectUrl);
        Assert.False(vm.IsLoadingDetails);
        Assert.Null(vm.DetailsError);
        search.Verify(s => s.GetDetailsAsync("Newtonsoft.Json", "13.0.3", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task APackageThatSaysNothing_Should_ShowNoDetailSections()
    {
        var (vm, _) = CreateVm((_, _, _) => Task.FromResult<PackageDetails?>(null));

        await SelectAsync(vm);

        Assert.False(vm.HasDetailsDependencies);
        Assert.False(vm.DetailsHaveNoDependencies); // nothing is known, so nothing is claimed
        Assert.False(vm.HasDetailsLicense);
        Assert.False(vm.HasDetailsProjectUrl);
        Assert.False(vm.IsLoadingDetails);
    }

    [Fact]
    public async Task ADescribedPackageWithoutDependencies_Should_SayItHasNone()
    {
        var (vm, _) = CreateVm((_, _, _) => Task.FromResult<PackageDetails?>(new PackageDetails([], null, null, null)));

        await SelectAsync(vm);

        Assert.True(vm.DetailsHaveNoDependencies);
        Assert.False(vm.HasDetailsDependencies);
    }

    [Fact]
    public async Task ALicenseGivenOnlyAsAnAddress_Should_BeShownAsThatAddress()
    {
        var (vm, _) = CreateVm((_, _, _) => Task.FromResult<PackageDetails?>(new PackageDetails([], null, "https://example.test/license", null)));

        await SelectAsync(vm);

        Assert.Equal("https://example.test/license", vm.DetailsLicense);
        Assert.True(vm.HasDetailsLicenseLink);
    }

    [Fact]
    public async Task AFailedLookup_Should_BeReported_WithoutStoppingThePackageFromBeingAdded()
    {
        var (vm, _) = CreateVm((_, _, _) => throw new InvalidOperationException("The package source could not be reached."));

        await SelectAsync(vm);

        Assert.Equal("Couldn't load the package details: The package source could not be reached.", vm.DetailsError);
        Assert.False(vm.IsLoadingDetails);
        Assert.False(vm.HasDetailsDependencies);
        Assert.True(vm.AddPackageCommand.CanExecute.FirstAsync().GetAwaiter().GetResult());
        vm.AddPackageCommand.Execute().Subscribe();
        Assert.True(vm.ApplyChanges());
    }

    [Fact]
    public async Task ChoosingAnotherVersion_Should_LoadThatVersionsDetails_AndDropAnAnswerForTheOldOne()
    {
        var late = new TaskCompletionSource<PackageDetails?>();
        var requested = new List<string?>();
        var firstRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _) = CreateVm((_, version, _) =>
        {
            lock (requested) requested.Add(version);
            if (version == "13.0.3") firstRequested.TrySetResult();
            return version == "13.0.3"
                ? late.Task
                : Task.FromResult<PackageDetails?>(new PackageDetails([], "Apache-2.0", null, null));
        });
        vm.SearchText = "newtonsoft";
        await vm.PendingSearch;
        vm.SelectedOnlinePackage = vm.OnlinePackages.Single();
        await vm.PendingVersions;
        await firstRequested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(vm.IsLoadingDetails);

        vm.SelectedVersion = "12.0.3";
        await vm.PendingDetails;
        late.SetResult(Full()); // the first answer arrives after the user moved on
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(["13.0.3", "12.0.3"], requested);
        Assert.Equal("Apache-2.0", vm.DetailsLicense);
        Assert.False(vm.HasDetailsDependencies);
        Assert.False(vm.IsLoadingDetails);
    }

    [Fact]
    public async Task GoingBackToAVersion_Should_UseTheEarlierAnswer()
    {
        var calls = 0;
        var (vm, _) = CreateVm((_, _, _) =>
        {
            calls++;
            return Task.FromResult<PackageDetails?>(Full());
        });
        await SelectAsync(vm);

        vm.SelectedVersion = "12.0.3";
        await vm.PendingDetails;
        vm.SelectedVersion = "13.0.3";
        await vm.PendingDetails;

        Assert.Equal(2, calls);
        Assert.Equal("MIT", vm.DetailsLicense);
    }

    [Fact]
    public async Task OpenLink_Should_OnlyOpenTheAddressesTheCardShows()
    {
        var (vm, _) = CreateVm((_, _, _) => Task.FromResult<PackageDetails?>(Full()));
        var opened = new List<Uri>();
        vm.LinkLauncher = uri =>
        {
            opened.Add(uri);
            return Task.CompletedTask;
        };
        await SelectAsync(vm);

        await vm.OpenLinkCommand.Execute("https://www.newtonsoft.com/json");
        await vm.OpenLinkCommand.Execute("https://licenses.nuget.org/MIT");
        await vm.OpenLinkCommand.Execute("https://elsewhere.test/");
        await vm.OpenLinkCommand.Execute("file:///etc/passwd");

        Assert.Equal(["https://www.newtonsoft.com/json", "https://licenses.nuget.org/MIT"], opened.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task ALinkThatCantBeOpened_Should_BeReported()
    {
        var (vm, _) = CreateVm((_, _, _) => Task.FromResult<PackageDetails?>(Full()));
        vm.LinkLauncher = _ => throw new InvalidOperationException("No application opened the address.");
        await SelectAsync(vm);

        await vm.OpenLinkCommand.Execute("https://www.newtonsoft.com/json");

        Assert.Equal("Couldn't open the link: No application opened the address.", vm.DetailsError);
    }
}
