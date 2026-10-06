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

    private static (DependenciesWindowViewModel Vm, Mock<IPackageSearchService> Search, Mock<ILocalPackageCacheService> Cache)
        CreateVm(params PackageSearchResult[] results)
    {
        var search = new Mock<IPackageSearchService>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PackageSearchResult>)results);
        search.Setup(s => s.GetVersionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Returns([]);
        return (new DependenciesWindowViewModel(search.Object, cache.Object, TimeSpan.Zero), search, cache);
    }

    private static async Task SearchAsync(DependenciesWindowViewModel vm, string text)
    {
        vm.SearchText = text;
        await vm.PendingSearch;
    }

    private static async Task SelectAsync(DependenciesWindowViewModel vm, string id)
    {
        vm.SelectedPackage = vm.Packages.Single(p => p.Id == id);
        await vm.PendingVersions;
    }

    private static bool CanExecute(ICommand command) => command.CanExecute(null);

    private static void Execute(ICommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
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
        Assert.True(vm.AddNamespace("System.Linq"));
        Execute(vm.Namespaces.Single(n => n.Value == "System.Text.Json").RemoveCommand);

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
        Assert.False(vm.AddNamespace("System.Linq")); // no script: no edits
        Assert.False(CanExecute(vm.AddNamespaceCommand));

        var fm = NewFrontMatter();
        vm.SetFrontMatter(fm);
        Assert.True(vm.HasFrontMatter);
        Assert.True(CanExecute(vm.AddNamespaceCommand));
        Assert.True(vm.AddNamespace("System.Linq"));

        vm.SetFrontMatter(fm); // the live stream re-sends the same object
        Assert.Contains(vm.Namespaces, n => n.Value == "System.Linq");
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
        Assert.True(vm.Packages.Single().IsInScript);
        Assert.False(vm.HasChanges);
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
        vm.Mode = PackageListMode.InScript;
        await SelectAsync(vm, "Existing.Package");

        Execute(vm.RemovePackageCommand);

        Assert.Empty(vm.Packages);
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
        vm.Mode = PackageListMode.InScript;

        await SelectAsync(vm, "Ranged.Package");

        Assert.Equal("[1.0,2.0)", vm.SelectedVersion);
        Assert.Equal(["[1.0,2.0)", "3.0.0", "2.0.0"], vm.AvailableVersions);
        Assert.False(CanExecute(vm.UpdatePackageCommand));
        Assert.False(vm.ApplyChanges());
        Assert.Equal("[1.0,2.0)", fm.Packages["Ranged.Package"].Version);
        vm.Dispose();
    }

    [Fact]
    public async Task SwitchingModes_Should_KeepTheSelectedPackageAndChosenVersion()
    {
        var (vm, search, _) = CreateVm(new PackageSearchResult("Existing.Package", "2.0.0", "desc", 5));
        search.Setup(s => s.GetVersionsAsync("Existing.Package", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["2.0.0", "1.5.0", "1.2.3"]);
        vm.SetFrontMatter(NewFrontMatter());
        await SearchAsync(vm, "Existing");
        await SelectAsync(vm, "Existing.Package");
        vm.SelectedVersion = "1.5.0";

        vm.Mode = PackageListMode.InScript;
        await vm.PendingVersions;

        Assert.Equal("Existing.Package", vm.SelectedPackage?.Id);
        Assert.Equal("1.5.0", vm.SelectedVersion);
        vm.Dispose();
    }

    [Fact]
    public async Task CachedPackage_Should_BeAddable_WhenTheVersionLookupFails()
    {
        var (vm, search, cache) = CreateVm();
        cache.Setup(c => c.GetCachedPackages()).Returns([new CachedPackageInfo("Cached.Package", "4.5.6")]);
        search.Setup(s => s.GetVersionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("offline"));
        var fm = NewFrontMatter();
        vm.SetFrontMatter(fm);

        vm.Mode = PackageListMode.Cached;
        await SelectAsync(vm, "Cached.Package");

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

        vm.SelectedPackage = vm.Packages.Single(p => p.Id == "A.Package");
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

    // ── Search ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LateSearchResult_Should_NotReplaceANewerSearch()
    {
        var (vm, search, _) = CreateVm();
        var slow = new TaskCompletionSource<IReadOnlyList<PackageSearchResult>>();
        search.Setup(s => s.SearchAsync("old", It.IsAny<CancellationToken>())).Returns(slow.Task);
        search.Setup(s => s.SearchAsync("new", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PackageSearchResult>)[new PackageSearchResult("New.Package", "1.0.0", "", 0)]);

        vm.SearchText = "old";
        var oldSearch = vm.PendingSearch;
        Assert.True(vm.IsSearching);
        await SearchAsync(vm, "new");

        slow.SetResult([new PackageSearchResult("Old.Package", "1.0.0", "", 0)]);
        await oldSearch;

        Assert.Equal(["New.Package"], vm.Packages.Select(p => p.Id));
        Assert.False(vm.IsSearching);
        vm.Dispose();
    }

    // Review finding: a search pending in Browse, then the text changed
    // while a local mode was shown, then back to Browse. The service ignores cancellation and answers
    // "first" last — only "second" may be shown, and it must actually be requested.
    [Fact]
    public async Task ReturningToBrowse_AfterTheFilterChangedInALocalMode_Should_SearchTheCurrentText_AndDropTheOldAnswer()
    {
        var (vm, search, _) = CreateVm();
        var first = new TaskCompletionSource<IReadOnlyList<PackageSearchResult>>();
        search.Setup(s => s.SearchAsync("first", It.IsAny<CancellationToken>())).Returns(first.Task);
        search.Setup(s => s.SearchAsync("second", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PackageSearchResult>)[new PackageSearchResult("Second.Package", "1.0.0", "", 0)]);

        vm.SearchText = "first";
        var firstSearch = vm.PendingSearch;
        Assert.True(vm.IsSearching);

        vm.Mode = PackageListMode.Cached;
        Assert.False(vm.IsSearching);
        vm.SearchText = "second"; // local filter only
        search.Verify(s => s.SearchAsync("second", It.IsAny<CancellationToken>()), Times.Never);

        vm.Mode = PackageListMode.Browse;
        await vm.PendingSearch;
        first.SetResult([new PackageSearchResult("First.Package", "1.0.0", "", 0)]);
        await firstSearch;

        search.Verify(s => s.SearchAsync("second", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["Second.Package"], vm.Packages.Select(p => p.Id));
        Assert.False(vm.IsSearching);
        vm.Dispose();
    }

    [Fact]
    public async Task ReturningToBrowse_WhileTheOldSearchIsStillPending_Should_NotShowItsAnswer()
    {
        var (vm, search, _) = CreateVm();
        var first = new TaskCompletionSource<IReadOnlyList<PackageSearchResult>>();
        var calls = 0;
        search.Setup(s => s.SearchAsync("first", It.IsAny<CancellationToken>()))
            .Callback(() => calls++)
            .Returns(() => calls == 1
                ? first.Task
                : Task.FromResult<IReadOnlyList<PackageSearchResult>>([new PackageSearchResult("Fresh.Package", "1.0.0", "", 0)]));

        vm.SearchText = "first";
        var stale = vm.PendingSearch;
        vm.Mode = PackageListMode.InScript;
        vm.Mode = PackageListMode.Browse; // same text: searched again, the pending one is stale
        await vm.PendingSearch;

        first.SetResult([new PackageSearchResult("Stale.Package", "1.0.0", "", 0)]);
        await stale;

        Assert.Equal(2, calls);
        Assert.Equal(["Fresh.Package"], vm.Packages.Select(p => p.Id));
        Assert.False(vm.IsSearching);
        vm.Dispose();
    }

    [Fact]
    public async Task ClearingTheFilterInALocalMode_ThenReturningToBrowse_Should_ResetResultsAndLoading()
    {
        var (vm, search, _) = CreateVm(new PackageSearchResult("Json.Package", "1.0.0", "", 0));
        await SearchAsync(vm, "json");
        Assert.Single(vm.Packages);

        var pending = new TaskCompletionSource<IReadOnlyList<PackageSearchResult>>();
        search.Setup(s => s.SearchAsync("jsonx", It.IsAny<CancellationToken>())).Returns(pending.Task);
        vm.SearchText = "jsonx";
        var stale = vm.PendingSearch;

        vm.Mode = PackageListMode.Cached;
        vm.SearchText = "";
        vm.Mode = PackageListMode.Browse;

        pending.SetResult([new PackageSearchResult("Stale.Package", "1.0.0", "", 0)]);
        await stale;

        Assert.Empty(vm.Packages);
        Assert.False(vm.IsSearching);
        Assert.False(vm.HasListError);
        Assert.NotNull(vm.ListMessage); // the idle "type to search" state
        search.Verify(s => s.SearchAsync("", It.IsAny<CancellationToken>()), Times.Never);
        vm.Dispose();
    }

    [Fact]
    public async Task LocalModes_Should_NeverStartANetworkSearch()
    {
        var (vm, search, _) = CreateVm();
        vm.SetFrontMatter(NewFrontMatter());

        vm.Mode = PackageListMode.InScript;
        vm.SearchText = "Existing";
        vm.Mode = PackageListMode.Cached;
        vm.SearchText = "Other";
        await vm.PendingSearch;

        search.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.Dispose();
    }

    [Fact]
    public void ClearingTheSearch_Should_ResetLoadingAndResults()
    {
        var (vm, search, _) = CreateVm();
        var never = new TaskCompletionSource<IReadOnlyList<PackageSearchResult>>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(never.Task);

        vm.SearchText = "json";
        Assert.True(vm.IsSearching);

        Execute(vm.ClearSearchCommand);

        Assert.Equal("", vm.SearchText);
        Assert.False(vm.IsSearching);
        Assert.Empty(vm.Packages);
        Assert.False(vm.HasListError);
        Assert.NotNull(vm.ListMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task SearchFailure_Should_BeShownAsAnError_NotAsNoResults_AndRetryOnRequest()
    {
        var (vm, search, _) = CreateVm();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("None of the 2 configured package sources could be reached."));

        await SearchAsync(vm, "json");

        Assert.True(vm.HasListError);
        Assert.Contains("could be reached", vm.ListError);
        Assert.Null(vm.ListMessage);

        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PackageSearchResult>)[new PackageSearchResult("Json.Package", "1.0.0", "", 0)]);
        Execute(vm.RetryListCommand);
        await vm.PendingSearch;

        Assert.False(vm.HasListError);
        Assert.Single(vm.Packages);
        vm.Dispose();
    }

    [Fact]
    public async Task CacheFailure_Should_NotReplaceTheSearchStatus()
    {
        var (vm, _, cache) = CreateVm(new PackageSearchResult("Json.Package", "1.0.0", "", 0));
        cache.Setup(c => c.GetCachedPackages()).Throws(new IOException("cache unreadable"));
        await SearchAsync(vm, "json");

        vm.Mode = PackageListMode.Cached;
        Assert.Contains("cache unreadable", vm.ListError);

        vm.Mode = PackageListMode.Browse;
        Assert.False(vm.HasListError);
        Assert.Single(vm.Packages);
        vm.Dispose();
    }

    [Fact]
    public async Task Dispose_Should_CancelPendingWork_AndIgnoreLateAnswers()
    {
        var (vm, search, _) = CreateVm();
        CancellationToken seen = default;
        var slow = new TaskCompletionSource<IReadOnlyList<PackageSearchResult>>();
        search.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((_, ct) => seen = ct)
            .Returns(slow.Task);

        vm.SearchText = "json";
        var pending = vm.PendingSearch;
        vm.Dispose();

        Assert.True(seen.IsCancellationRequested);
        slow.SetResult([new PackageSearchResult("Late.Package", "1.0.0", "", 0)]);
        await pending;
        Assert.Empty(vm.Packages);
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

        vm.NewReference = "  ../lib/Other.csproj ";
        Assert.Null(vm.ReferenceError); // editing clears the message
        Execute(vm.AddReferenceCommand);
        Assert.Equal("", vm.NewReference);
        Assert.Equal(["../lib/My.dll", "../lib/Other.csproj"], vm.References.Select(r => r.Value));
        vm.Dispose();
    }

    [Theory]
    [InlineData("", "Enter a namespace")]
    [InlineData("using System.Linq", "can't contain spaces")]
    [InlineData("System.Text.Json", "already imported")]
    public void AddNamespace_Should_RejectInvalidInput_WithAnInlineMessage(string input, string expected)
    {
        var (vm, _, _) = CreateVm();
        vm.SetFrontMatter(NewFrontMatter());

        vm.NewNamespace = input;
        Execute(vm.AddNamespaceCommand);

        Assert.Contains(expected, vm.NamespaceError);
        Assert.Equal(input, vm.NewNamespace);
        Assert.Equal(["System.Text.Json"], vm.Namespaces.Select(n => n.Value));
        vm.Dispose();
    }

    [Fact]
    public void AddNamespace_Should_TrimAndBeCaseSensitive()
    {
        var (vm, _, _) = CreateVm();
        vm.SetFrontMatter(NewFrontMatter());

        Assert.True(vm.AddNamespace("  system.text.json  "));
        Assert.True(vm.AddNamespace("@Generated.Ns_1"));

        Assert.Equal(["System.Text.Json", "system.text.json", "@Generated.Ns_1"], vm.Namespaces.Select(n => n.Value));
        vm.Dispose();
    }
}
