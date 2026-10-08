using System.Windows.Input;
using Moq;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.NonUI.Tests;

// DependenciesWindowViewModel without a window: the draft/OK/Cancel transaction, package
// add/update/remove, versions, stale async answers and validation. Debounce is zero so tests await
// the VM's own PendingSearch/PendingVersions instead of sleeping.
public class DependenciesWindowViewModelLogicTests
{
    private static RpadFrontMatter NewFrontMatter(Action<RpadFrontMatter>? configure = null)
    {
        var fm = new RpadFrontMatter
        {
            Version = "1.0",
            Kind = RpadKind.Program,
            TargetSdk = TargetSdk.Default,
            Packages = new Dictionary<string, Package> { ["Existing.Package"] = new("1.2.3") },
            References = ["../lib/My.dll"],
            Namespaces = ["System.Text.Json"],
        };
        configure?.Invoke(fm);
        return fm;
    }

    private static PackageSearchPage Page(params PackageSearchResult[] results) => new(results, HasMore: false);

    private static Mock<INuGetSourceStore> SourceStore(params NuGetSource[] sources)
    {
        var store = new Mock<INuGetSourceStore>();
        store.Setup(s => s.Load()).Returns(sources.Length > 0
            ? sources
            : [new NuGetSource("nuget.org", "https://api.nuget.org/v3/index.json")]);
        return store;
    }

    private static (DependenciesWindowViewModel Vm, Mock<IPackageSearchService> Search, Mock<ILocalPackageCacheService> Cache)
        CreateVm(params PackageSearchResult[] results) => CreateVm(cached: [], results);

    // The local cache is read when the VM is created, so its content is set up first.
    private static (DependenciesWindowViewModel Vm, Mock<IPackageSearchService> Search, Mock<ILocalPackageCacheService> Cache)
        CreateVm(IReadOnlyList<CachedPackageInfo> cached, params PackageSearchResult[] results)
    {
        var search = new Mock<IPackageSearchService>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(results));
        search.Setup(s => s.GetVersionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Returns(cached);
        return (new DependenciesWindowViewModel(search.Object, cache.Object, SourceStore().Object, TimeSpan.Zero), search, cache);
    }

    private static async Task SearchAsync(DependenciesWindowViewModel vm, string text)
    {
        vm.SearchText = text;
        await vm.PendingSearch;
    }

    private static async Task SelectAsync(DependenciesWindowViewModel vm, string id)
    {
        vm.SelectedOnlinePackage = vm.OnlinePackages.Single(p => p.Id == id);
        await vm.PendingVersions;
    }

    private static async Task SelectLocalAsync(DependenciesWindowViewModel vm, string id)
    {
        vm.SelectedLocalPackage = vm.LocalPackages.Single(p => p.Id == id);
        await vm.PendingVersions;
    }

    private static bool CanExecute(ICommand command) => command.CanExecute(null);

    private static void Execute(ICommand command, object? parameter = null)
    {
        Assert.True(command.CanExecute(parameter));
        command.Execute(parameter);
    }

    // ── Transaction ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Edits_Should_StayInTheDraft_UntilApplyChanges()
    {
        var (vm, _, _) = CreateVm(new PackageSearchResult("Newtonsoft.Json", "13.0.3", "JSON", 100));
        var fm = NewFrontMatter();
        vm.SetFrontMatter(fm);

        await SearchAsync(vm, "json");
        await SelectAsync(vm, "Newtonsoft.Json");
        Execute(vm.AddPackageCommand);
        Assert.True(vm.AddReference("$(NuGetPackageRoot)/x.dll"));
        vm.NamespacesText = "System.Linq";

        // Nothing reached the original yet.
        Assert.Equal(["Existing.Package"], fm.Packages.Keys);
        Assert.Equal(["../lib/My.dll"], fm.References!);
        Assert.Equal(["System.Text.Json"], fm.Namespaces!);
        Assert.True(vm.HasChanges);

        Assert.True(vm.ApplyChanges());

        Assert.Equal("13.0.3", fm.Packages["Newtonsoft.Json"].Version);
        Assert.Equal("1.2.3", fm.Packages["Existing.Package"].Version);
        Assert.Equal(["../lib/My.dll", "$(NuGetPackageRoot)/x.dll"], fm.References!);
        Assert.Equal(["System.Linq"], fm.Namespaces!);
        // Fields outside the dialog's scope survive untouched.
        Assert.Equal(RpadKind.Program, fm.Kind);
        Assert.Equal("1.0", fm.Version);

        // Applies once only.
        Assert.False(vm.ApplyChanges());
        vm.Dispose();
    }

    [Fact]
    public void ApplyChanges_WithoutEdits_Should_LeaveTheOriginalCollectionsAlone()
    {
        var (vm, _, _) = CreateVm();
        var fm = NewFrontMatter(f => f.Namespaces = null);
        var packages = fm.Packages;
        vm.SetFrontMatter(fm);

        Assert.False(vm.HasChanges);
        Assert.False(vm.ApplyChanges());
        Assert.Same(packages, fm.Packages);
        Assert.Null(fm.Namespaces);
        vm.Dispose();
    }

    [Fact]
    public void SetFrontMatter_WithTheSameObject_Should_KeepTheDraft_AndNullToLoaded_Should_EnableEditing()
    {
        var (vm, _, _) = CreateVm();
        Assert.False(vm.HasFrontMatter);
        vm.NamespacesText = "System.Linq"; // no script: nothing to apply
        Assert.False(vm.HasChanges);
        Assert.False(vm.ApplyChanges());

        var fm = NewFrontMatter();
        vm.SetFrontMatter(fm);
        Assert.True(vm.HasFrontMatter);
        Assert.Equal("System.Text.Json", vm.NamespacesText); // loading replaces the draft text
        vm.NamespacesText = "System.Text.Json\nSystem.Linq";

        vm.SetFrontMatter(fm); // the live stream re-sends the same object
        Assert.Equal("System.Text.Json\nSystem.Linq", vm.NamespacesText);
        Assert.True(vm.HasChanges);
        vm.Dispose();

    }

    // ── Packages ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddingAPackage_ThatDiffersOnlyByCase_Should_NotCreateADuplicate()
    {
        var (vm, _, _) = CreateVm(new PackageSearchResult("existing.package", "2.0.0", "", 0));
        var fm = NewFrontMatter();
        vm.SetFrontMatter(fm);

        await SearchAsync(vm, "existing");
        await SelectAsync(vm, "existing.package");

        Assert.True(vm.IsSelectedInScript);
        Assert.Equal("1.2.3", vm.SelectedScriptVersion);
        Assert.Equal("1.2.3", vm.SelectedVersion); // the script's own version is preselected
        Assert.False(CanExecute(vm.AddPackageCommand));
        Assert.False(CanExecute(vm.UpdatePackageCommand)); // same version: nothing to update
        var row = vm.OnlinePackages.Single();
        Assert.True(row.IsInScript);
        Assert.False(row.CanAdd);
        Execute(vm.UpdateRowCommand, row); // the row offers its newer version instead
        Assert.Equal(["Existing.Package"], vm.LocalPackages.Where(p => p.IsInScript).Select(p => p.Id));
        vm.Dispose();
    }

    [Fact]
    public async Task UpdateAndRemove_Should_ChangeOnlyTheDraft_AndKeepTheOriginalIdSpelling()
    {
        var (vm, search, _) = CreateVm(new PackageSearchResult("existing.package", "2.0.0", "", 0));
        search.Setup(s => s.GetVersionsAsync("existing.package", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["2.0.0", "1.2.3", "1.0.0"]);
        var fm = NewFrontMatter();
        vm.SetFrontMatter(fm);

        await SearchAsync(vm, "existing");
        await SelectAsync(vm, "existing.package");
        vm.SelectedVersion = "2.0.0";
        Execute(vm.UpdatePackageCommand);

        Assert.Equal("2.0.0", vm.SelectedScriptVersion);
        Assert.False(CanExecute(vm.UpdatePackageCommand));
        Assert.Equal("1.2.3", fm.Packages["Existing.Package"].Version);

        vm.ApplyChanges();
        Assert.Equal(["Existing.Package"], fm.Packages.Keys);
        Assert.Equal("2.0.0", fm.Packages["Existing.Package"].Version);
        vm.Dispose();
    }

    [Fact]
    public async Task Remove_Should_DropThePackage_OnlyAfterApply()
    {
        var (vm, _, _) = CreateVm();
        var fm = NewFrontMatter();
        vm.SetFrontMatter(fm);
        await SelectLocalAsync(vm, "Existing.Package");

        Execute(vm.RemovePackageCommand);

        Assert.DoesNotContain(vm.LocalPackages, p => p.Id == "Existing.Package");
        Assert.Contains("Existing.Package", fm.Packages.Keys);
        vm.ApplyChanges();
        Assert.Empty(fm.Packages);
        vm.Dispose();
    }

    [Fact]
    public async Task CurrentVersionMissingFromTheServer_Should_StaySelected_AndBeKept()
    {
        var (vm, search, _) = CreateVm();
        search.Setup(s => s.GetVersionsAsync("Ranged.Package", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["3.0.0", "2.0.0"]);
        var fm = NewFrontMatter(f => f.Packages = new Dictionary<string, Package>
        {
            ["Ranged.Package"] = new("[1.0,2.0)"),
        });
        vm.SetFrontMatter(fm);

        await SelectLocalAsync(vm, "Ranged.Package");

        Assert.Equal("[1.0,2.0)", vm.SelectedVersion);
        Assert.Equal(["[1.0,2.0)", "3.0.0", "2.0.0"], vm.AvailableVersions);
        Assert.False(CanExecute(vm.UpdatePackageCommand));
        Assert.False(vm.ApplyChanges());
        Assert.Equal("[1.0,2.0)", fm.Packages["Ranged.Package"].Version);
        vm.Dispose();
    }

    [Fact]
    public async Task SelectingTheSamePackageInTheOtherList_Should_KeepTheChosenVersion()
    {
        var (vm, search, _) = CreateVm(new PackageSearchResult("Existing.Package", "2.0.0", "desc", 5));
        search.Setup(s => s.GetVersionsAsync("Existing.Package", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["2.0.0", "1.5.0", "1.2.3"]);
        vm.SetFrontMatter(NewFrontMatter());
        await SearchAsync(vm, "Existing");
        await SelectAsync(vm, "Existing.Package");
        vm.SelectedVersion = "1.5.0";

        await SelectLocalAsync(vm, "Existing.Package");

        Assert.Equal("Existing.Package", vm.SelectedPackage?.Id);
        Assert.Null(vm.SelectedOnlinePackage); // one selection at a time across both lists
        Assert.Equal("1.5.0", vm.SelectedVersion);
        vm.Dispose();
    }

    [Fact]
    public async Task CachedPackage_Should_BeAddable_WhenTheVersionLookupFails()
    {
        var (vm, search, _) = CreateVm(cached: [new CachedPackageInfo("Cached.Package", "4.5.6")]);
        search.Setup(s => s.GetVersionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("offline"));
        var fm = NewFrontMatter();
        vm.SetFrontMatter(fm);

        await SelectLocalAsync(vm, "Cached.Package");

        Assert.True(vm.HasVersionsError);
        Assert.Contains("offline", vm.VersionsError);
        Assert.Equal("4.5.6", vm.SelectedVersion);
        Execute(vm.AddPackageCommand);
        vm.ApplyChanges();
        Assert.Equal("4.5.6", fm.Packages["Cached.Package"].Version);

        // Retry on request.
        search.Setup(s => s.GetVersionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["5.0.0", "4.5.6"]);
        Execute(vm.RetryVersionsCommand);
        await vm.PendingVersions;
        Assert.False(vm.HasVersionsError);
        Assert.Equal(["5.0.0", "4.5.6"], vm.AvailableVersions);
        Assert.Equal("4.5.6", vm.SelectedVersion);
        vm.Dispose();
    }

    [Fact]
    public async Task SelectingAnotherPackage_Should_IgnoreTheFirstPackagesLateVersions()
    {
        var (vm, search, _) = CreateVm(
            new PackageSearchResult("A.Package", "1.0.0", "", 0),
            new PackageSearchResult("B.Package", "2.0.0", "", 0));
        var slowA = new TaskCompletionSource<IReadOnlyList<string>>();
        search.Setup(s => s.GetVersionsAsync("A.Package", It.IsAny<CancellationToken>())).Returns(slowA.Task);
        search.Setup(s => s.GetVersionsAsync("B.Package", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["2.0.0"]);
        vm.SetFrontMatter(NewFrontMatter());
        await SearchAsync(vm, "package");

        vm.SelectedOnlinePackage = vm.OnlinePackages.Single(p => p.Id == "A.Package");
        var pendingA = vm.PendingVersions;
        Assert.True(vm.IsLoadingVersions);
        await SelectAsync(vm, "B.Package");

        slowA.SetResult(["9.9.9", "1.0.0"]); // service ignored the cancellation
        await pendingA;

        Assert.Equal(["2.0.0"], vm.AvailableVersions);
        Assert.Equal("2.0.0", vm.SelectedVersion);
        Assert.False(vm.IsLoadingVersions);
        vm.Dispose();
    }

    // ── Local column ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LocalColumn_Should_ListTheScriptsPackagesFirst_ThenCachedOnes_AndFilterWithoutSearching()
    {
        var (vm, search, _) = CreateVm(cached:
        [
            new CachedPackageInfo("Existing.Package", "9.0.0"), // in the script: listed once, as the script's
            new CachedPackageInfo("Cached.Json", "4.5.6"),
            new CachedPackageInfo("Cached.Xml", "1.0.0"),
        ]);
        vm.SetFrontMatter(NewFrontMatter());

        Assert.Equal(["Existing.Package", "Cached.Json", "Cached.Xml"], vm.LocalPackages.Select(p => p.Id));
        var script = vm.LocalPackages[0];
        Assert.True(script.IsInScript);
        Assert.Equal("1.2.3", script.Version);
        Assert.True(script.CanRemove);
        Assert.False(script.CanAdd);
        Assert.True(vm.LocalPackages[1].CanAdd);

        vm.LocalFilter = "json";
        Assert.Equal(["Cached.Json"], vm.LocalPackages.Select(p => p.Id));

        Execute(vm.AddRowCommand, vm.LocalPackages[0]);
        Assert.Equal(["Cached.Json"], vm.LocalPackages.Select(p => p.Id));
        Assert.True(vm.LocalPackages[0].IsInScript);
        Assert.Equal("4.5.6", vm.LocalPackages[0].ScriptVersion);

        await vm.PendingSearch;
        search.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
        vm.Dispose();
    }

    [Fact]
    public void WithoutAScript_Rows_Should_OfferNoActions()
    {
        var (vm, _, _) = CreateVm(cached: [new CachedPackageInfo("Cached.Json", "4.5.6")]);

        var row = Assert.Single(vm.LocalPackages);
        Assert.False(row.CanAdd);
        Execute(vm.AddRowCommand, row); // no-op without a script
        Assert.False(vm.HasChanges);

        vm.SetFrontMatter(NewFrontMatter());
        Assert.True(vm.LocalPackages.Single(p => p.Id == "Cached.Json").CanAdd);
        vm.Dispose();
    }

    [Fact]
    public void CacheFailure_Should_ShowALocalError_AndKeepTheScriptsPackages()
    {
        var search = new Mock<IPackageSearchService>();
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Throws(new IOException("cache unreadable"));
        var vm = new DependenciesWindowViewModel(search.Object, cache.Object, SourceStore().Object, TimeSpan.Zero);
        vm.SetFrontMatter(NewFrontMatter());

        Assert.Contains("cache unreadable", vm.LocalError);
        Assert.Equal(["Existing.Package"], vm.LocalPackages.Select(p => p.Id));
        Assert.False(vm.HasSearchError); // the online column is unaffected

        cache.Setup(c => c.GetCachedPackages()).Returns([new CachedPackageInfo("Cached.Json", "4.5.6")]);
        Execute(vm.RetryLocalCommand);
        Assert.False(vm.HasLocalError);
        Assert.Contains(vm.LocalPackages, p => p.Id == "Cached.Json");
        vm.Dispose();
    }

    // ── Online column: rows, sources, prerelease, paging ──────────────────────

    [Fact]
    public async Task OnlineRowAdd_Should_AddTheResultsVersion()
    {
        var (vm, _, _) = CreateVm(new PackageSearchResult("Newtonsoft.Json", "13.0.3", "JSON", 100));
        var fm = NewFrontMatter();
        vm.SetFrontMatter(fm);
        await SearchAsync(vm, "json");

        var row = vm.OnlinePackages.Single();
        Execute(vm.AddRowCommand, row);

        Assert.True(row.IsInScript);
        Assert.False(row.CanAdd);
        Assert.Contains(vm.LocalPackages, p => p.Id == "Newtonsoft.Json" && p.IsInScript);
        vm.ApplyChanges();
        Assert.Equal("13.0.3", fm.Packages["Newtonsoft.Json"].Version);
        vm.Dispose();
    }

    [Fact]
    public async Task Search_Should_UseTheSelectedSource_AndPrereleaseSetting()
    {
        var search = new Mock<IPackageSearchService>();
        var seen = new List<PackageSearchOptions>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, PackageSearchOptions, CancellationToken>((_, o, _) => seen.Add(o))
            .ReturnsAsync(Page());
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Returns([]);
        var store = SourceStore(
            new NuGetSource("nuget.org", "https://api.nuget.org/v3/index.json"),
            new NuGetSource("disabled", "https://disabled.example/v3/index.json", IsEnabled: false),
            new NuGetSource("artifactory", "https://artifactory.example/v3/index.json"));
        var vm = new DependenciesWindowViewModel(search.Object, cache.Object, store.Object, TimeSpan.Zero);

        Assert.Equal(["All sources", "nuget.org", "artifactory", "(configure sources...)"],
            vm.SourceOptions.Select(o => o.Label));
        Assert.Same(PackageSourceOption.AllSources, vm.SelectedSourceOption);

        await SearchAsync(vm, "json");
        vm.SelectedSourceOption = vm.SourceOptions.Single(o => o.SourceName == "artifactory");
        await vm.PendingSearch;
        vm.IncludePrerelease = true;
        await vm.PendingSearch;
        vm.SelectedSourceOption = PackageSourceOption.Configure; // a command item, never selected
        await vm.PendingSearch;

        Assert.Equal(
            [new PackageSearchOptions(), new PackageSearchOptions("artifactory"), new PackageSearchOptions("artifactory", IncludePrerelease: true)],
            seen);
        Assert.Equal("artifactory", vm.SelectedSourceOption.SourceName);
        vm.Dispose();
    }

    [Fact]
    public async Task ReloadSources_Should_KeepTheSelectedSource_OrFallBackToAllSources()
    {
        var store = SourceStore(new NuGetSource("a", "https://a.example/v3/index.json"), new NuGetSource("b", "https://b.example/v3/index.json"));
        var search = new Mock<IPackageSearchService>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page());
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Returns([]);
        var vm = new DependenciesWindowViewModel(search.Object, cache.Object, store.Object, TimeSpan.Zero);

        vm.SelectedSourceOption = vm.SourceOptions.Single(o => o.SourceName == "b");
        store.Setup(s => s.Load()).Returns([new NuGetSource("B", "https://b.example/v3/index.json"), new NuGetSource("c", "https://c.example/v3/index.json")]);
        vm.ReloadSources();
        Assert.Equal("B", vm.SelectedSourceOption.SourceName); // names are case-insensitive

        store.Setup(s => s.Load()).Returns([new NuGetSource("c", "https://c.example/v3/index.json")]);
        vm.ReloadSources();
        Assert.Same(PackageSourceOption.AllSources, vm.SelectedSourceOption);
        Assert.Equal(["All sources", "c", "(configure sources...)"], vm.SourceOptions.Select(o => o.Label));

        store.Setup(s => s.Load()).Throws(new IOException("locked"));
        vm.ReloadSources();
        Assert.Contains("locked", vm.SourcesError);
        await vm.PendingSearch;
        vm.Dispose();
    }

    [Fact]
    public async Task IncludePrerelease_Should_FilterTheServerVersions_ButKeepKnownOnes()
    {
        var (vm, search, _) = CreateVm(new PackageSearchResult("Existing.Package", "2.0.0", "", 0));
        search.Setup(s => s.GetVersionsAsync("Existing.Package", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["3.0.0-beta.1", "2.0.0", "1.2.3", "1.0.0-rc+build"]);
        vm.SetFrontMatter(NewFrontMatter(f => f.Packages["Existing.Package"] = new Package("1.0.0-rc+build")));
        await SearchAsync(vm, "existing");
        await SelectAsync(vm, "Existing.Package");

        Assert.Equal(["2.0.0", "1.2.3", "1.0.0-rc+build"], vm.AvailableVersions); // the script's prerelease stays

        vm.IncludePrerelease = true;
        await vm.PendingSearch;
        Assert.Equal(["3.0.0-beta.1", "2.0.0", "1.2.3", "1.0.0-rc+build"], vm.AvailableVersions);
        Assert.Equal("1.0.0-rc+build", vm.SelectedVersion);
        vm.Dispose();
    }

    [Theory]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.0-beta", true)]
    [InlineData("1.0.0+build-7", false)]
    [InlineData("1.0.0-rc.1+build", true)]
    public void IsPrerelease_Should_IgnoreBuildMetadata(string version, bool expected)
        => Assert.Equal(expected, DependenciesWindowViewModel.IsPrerelease(version));

    [Fact]
    public async Task Paging_Should_RequestTheNextAndPreviousPages_AndResetOnANewQuery()
    {
        var search = new Mock<IPackageSearchService>();
        var skips = new List<int>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, PackageSearchOptions, CancellationToken>((_, o, _) => skips.Add(o.Skip))
            .ReturnsAsync(() => new PackageSearchPage([new PackageSearchResult($"P{skips[^1]}", "1.0.0", "", 0)], HasMore: skips[^1] < 20));
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Returns([]);
        var vm = new DependenciesWindowViewModel(search.Object, cache.Object, SourceStore().Object, TimeSpan.Zero);

        await SearchAsync(vm, "p");
        Assert.True(vm.HasNextPage);
        Assert.False(vm.HasPreviousPage);

        Execute(vm.NextPageCommand);
        await vm.PendingSearch;
        Assert.Equal(["P20"], vm.OnlinePackages.Select(p => p.Id));
        Assert.False(vm.HasNextPage);
        Assert.True(vm.HasPreviousPage);

        Execute(vm.PreviousPageCommand);
        await vm.PendingSearch;
        Execute(vm.NextPageCommand);
        await vm.PendingSearch;
        await SearchAsync(vm, "q");

        Assert.Equal([0, 20, 0, 20, 0], skips);
        Assert.False(vm.HasPreviousPage);
        vm.Dispose();
    }

    [Fact]
    public async Task PageChange_Should_DropTheOldPage_AndShowTheLoader_UntilTheNewPageArrives()
    {
        var search = new Mock<IPackageSearchService>();
        var secondPage = new TaskCompletionSource<PackageSearchPage>();
        search.Setup(s => s.SearchAsync("p", It.Is<PackageSearchOptions>(o => o.Skip == 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageSearchPage([new PackageSearchResult("Page1.Package", "1.0.0", "", 0)], HasMore: true));
        search.Setup(s => s.SearchAsync("p", It.Is<PackageSearchOptions>(o => o.Skip == 20), It.IsAny<CancellationToken>()))
            .Returns(secondPage.Task);
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Returns([]);
        var vm = new DependenciesWindowViewModel(search.Object, cache.Object, SourceStore().Object, TimeSpan.Zero);
        await SearchAsync(vm, "p");
        Assert.False(vm.ShowOnlineLoader);

        Execute(vm.NextPageCommand);

        Assert.Empty(vm.OnlinePackages); // the old page is gone at once
        Assert.True(vm.ShowOnlineLoader);
        Assert.Null(vm.OnlineMessage);
        Assert.False(vm.HasNextPage);
        Assert.False(vm.HasPreviousPage); // no blind second click while loading

        secondPage.SetResult(new PackageSearchPage([new PackageSearchResult("Page2.Package", "1.0.0", "", 0)], HasMore: false));
        await vm.PendingSearch;

        Assert.Equal(["Page2.Package"], vm.OnlinePackages.Select(p => p.Id));
        Assert.False(vm.ShowOnlineLoader);
        Assert.True(vm.HasPreviousPage);
        vm.Dispose();
    }

    // ── Search ─────────────────────────────────────────────────────────────────

    // UI jank report: the NuGet client does real synchronous work before its first await. The VM
    // must hand the call to the thread pool so typing (the caller = the UI thread) never waits on it.
    [Fact]
    public async Task Search_Should_RunOffTheCallingThread_SoASlowClientNeverBlocksTyping()
    {
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var search = new Mock<IPackageSearchService>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                started.Set();
                gate.Wait(TimeSpan.FromSeconds(10)); // blocking, like the client's synchronous start
                return Task.FromResult(Page(new PackageSearchResult("Json.Package", "1.0.0", "", 0)));
            });
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Returns([]);
        var vm = new DependenciesWindowViewModel(search.Object, cache.Object, SourceStore().Object, TimeSpan.Zero);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        vm.SearchText = "json";
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"setting the text blocked for {watch.Elapsed}");
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(vm.IsSearching);
        gate.Set();
        await vm.PendingSearch;
        Assert.Equal(["Json.Package"], vm.OnlinePackages.Select(p => p.Id));
        vm.Dispose();
    }

    [Fact]
    public async Task TypingQuickly_Should_SearchOnce_ForTheFinalText_AfterTheDebounce()
    {
        var search = new Mock<IPackageSearchService>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page());
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Returns([]);
        var vm = new DependenciesWindowViewModel(search.Object, cache.Object, SourceStore().Object,
            TimeSpan.FromMilliseconds(300));

        foreach (var text in new[] { "j", "js", "jso", "json" })
            vm.SearchText = text;
        Assert.False(vm.IsSearching); // nothing starts while typing
        await vm.PendingSearch;

        search.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
        search.Verify(s => s.SearchAsync("json", It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        vm.Dispose();
    }

    [Fact]
    public async Task LateSearchResult_Should_NotReplaceANewerSearch()
    {
        var (vm, search, _) = CreateVm();
        var slow = new TaskCompletionSource<PackageSearchPage>();
        search.Setup(s => s.SearchAsync("old", It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>())).Returns(slow.Task);
        search.Setup(s => s.SearchAsync("new", It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(new PackageSearchResult("New.Package", "1.0.0", "", 0)));

        vm.SearchText = "old";
        var oldSearch = vm.PendingSearch;
        Assert.True(vm.IsSearching);
        await SearchAsync(vm, "new");

        slow.SetResult(Page(new PackageSearchResult("Old.Package", "1.0.0", "", 0)));
        await oldSearch;

        Assert.Equal(["New.Package"], vm.OnlinePackages.Select(p => p.Id));
        Assert.False(vm.IsSearching);
        vm.Dispose();
    }

    [Fact]
    public async Task ChangingTheSourceWhileASearchIsPending_Should_DropTheOldAnswer()
    {
        var (vm, search, _) = CreateVm();
        var first = new TaskCompletionSource<PackageSearchPage>();
        search.Setup(s => s.SearchAsync("json", It.Is<PackageSearchOptions>(o => o.Source == null), It.IsAny<CancellationToken>()))
            .Returns(first.Task);
        search.Setup(s => s.SearchAsync("json", It.Is<PackageSearchOptions>(o => o.Source == "nuget.org"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(new PackageSearchResult("Fresh.Package", "1.0.0", "", 0)));

        vm.SearchText = "json";
        var stale = vm.PendingSearch;
        vm.SelectedSourceOption = vm.SourceOptions.Single(o => o.SourceName == "nuget.org");
        await vm.PendingSearch;

        first.SetResult(Page(new PackageSearchResult("Stale.Package", "1.0.0", "", 0)));
        await stale;

        Assert.Equal(["Fresh.Package"], vm.OnlinePackages.Select(p => p.Id));
        Assert.False(vm.IsSearching);
        vm.Dispose();
    }

    [Fact]
    public void ClearingTheSearch_Should_ResetLoadingAndResults()
    {
        var (vm, search, _) = CreateVm();
        var never = new TaskCompletionSource<PackageSearchPage>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .Returns(never.Task);

        vm.SearchText = "json";
        Assert.True(vm.IsSearching);

        Execute(vm.ClearSearchCommand);

        Assert.Equal("", vm.SearchText);
        Assert.False(vm.IsSearching);
        Assert.Empty(vm.OnlinePackages);
        Assert.False(vm.HasSearchError);
        Assert.NotNull(vm.OnlineMessage); // the idle "type to search" state
        vm.Dispose();
    }

    [Fact]
    public async Task SearchFailure_Should_BeShownAsAnError_NotAsNoResults_AndRetryOnRequest()
    {
        var (vm, search, _) = CreateVm();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("None of the 2 package sources could be reached."));

        await SearchAsync(vm, "json");

        Assert.True(vm.HasSearchError);
        Assert.Contains("could be reached", vm.SearchError);
        Assert.Null(vm.OnlineMessage);

        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(new PackageSearchResult("Json.Package", "1.0.0", "", 0)));
        Execute(vm.RetrySearchCommand);
        await vm.PendingSearch;

        Assert.False(vm.HasSearchError);
        Assert.Single(vm.OnlinePackages);
        vm.Dispose();
    }

    [Fact]
    public async Task Dispose_Should_CancelPendingWork_AndIgnoreLateAnswers()
    {
        var (vm, search, _) = CreateVm();
        var called = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new TaskCompletionSource<PackageSearchPage>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<PackageSearchOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, PackageSearchOptions, CancellationToken>((_, _, ct) => called.TrySetResult(ct))
            .Returns(slow.Task);

        vm.SearchText = "json";
        var pending = vm.PendingSearch;
        var seen = await called.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); // runs on the thread pool
        vm.Dispose();

        Assert.True(seen.IsCancellationRequested);
        slow.SetResult(Page(new PackageSearchResult("Late.Package", "1.0.0", "", 0)));
        await pending;
        Assert.Empty(vm.OnlinePackages);
        Assert.False(vm.ApplyChanges());
    }

    // ── References / namespaces ────────────────────────────────────────────────

    [Fact]
    public void References_Should_KeepValuesVerbatim_AndRejectEmptyOrExactDuplicates()
    {
        var (vm, _, _) = CreateVm();
        var fm = NewFrontMatter();
        vm.SetFrontMatter(fm);

        Assert.Equal(["../lib/My.dll"], vm.References.Select(r => r.Value)); // not made absolute

        Assert.False(vm.AddReference("   "));
        Assert.NotNull(vm.ReferenceError);
        Assert.False(vm.AddReference("../lib/My.dll"));
        Assert.Contains("already", vm.ReferenceError);

        vm.NewReference = "  $(MyDocuments)/Other.dll ";
        Assert.Null(vm.ReferenceError); // editing clears the message
        Execute(vm.AddReferenceCommand);
        Assert.Equal("", vm.NewReference);
        Assert.Equal(["../lib/My.dll", "$(MyDocuments)/Other.dll"], vm.References.Select(r => r.Value));
        vm.Dispose();
    }

    // Scripts compile in-process with Roslyn, which can't build another project (no MSBuild).
    [Theory]
    [InlineData("../lib/Lib.csproj")]
    [InlineData("../lib/Lib.VBPROJ")]
    [InlineData("../lib/Lib.fsproj")]
    public void ProjectFiles_Should_BeRejected_WithAnExplanation(string project)
    {
        var (vm, _, _) = CreateVm();
        vm.SetFrontMatter(NewFrontMatter());

        Assert.False(vm.AddReference(project));

        Assert.Contains("Project references aren't supported", vm.ReferenceError);
        Assert.Equal(["../lib/My.dll"], vm.References.Select(r => r.Value));
        vm.Dispose();
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("System.Linq\r\n\r\n  System.Text.Json  \n", new[] { "System.Linq", "System.Text.Json" })]
    [InlineData("using System.Linq;\nglobal using System.IO;\nImports System.Net", new[] { "System.Linq", "System.IO", "System.Net" })]
    [InlineData("System.Linq\nSystem.Linq\nsystem.linq", new[] { "System.Linq", "system.linq" })]
    [InlineData("@Generated.Ns_1", new[] { "@Generated.Ns_1" })]
    public void ParseNamespaces_Should_TakeOneNamespacePerLine(string text, string[] expected)
    {
        var (names, error) = DependenciesWindowViewModel.ParseNamespaces(text);

        Assert.Null(error);
        Assert.Equal(expected, names);
    }

    [Fact]
    public void AnInvalidNamespaceLine_Should_ShowAnInlineMessage_AndBlockApplying()
    {
        var (vm, _, _) = CreateVm();
        var fm = NewFrontMatter();
        vm.SetFrontMatter(fm);

        vm.NamespacesText = "System.Text.Json\nSystem Linq";

        Assert.True(vm.HasNamespaceError);
        Assert.Contains("\"System Linq\"", vm.NamespaceError);
        Assert.False(vm.ApplyChanges());
        Assert.Equal(["System.Text.Json"], fm.Namespaces!);

        vm.NamespacesText = "System.Text.Json\nSystem.Linq";
        Assert.False(vm.HasNamespaceError);
        Assert.True(vm.ApplyChanges());
        Assert.Equal(["System.Text.Json", "System.Linq"], fm.Namespaces!);
        vm.Dispose();
    }

    [Fact]
    public void ReformattingTheNamespaceText_WithoutChangingTheNames_Should_NotCountAsAChange()
    {
        var (vm, _, _) = CreateVm();
        vm.SetFrontMatter(NewFrontMatter());

        vm.NamespacesText = "\n  using System.Text.Json;  \n\n";

        Assert.False(vm.HasChanges);
        vm.Dispose();
    }
}
