using System.IO.Compression;
using Invoklet.Contracts;
using Invoklet.Languages.CSharp;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp;

// GetVersionsAsync/SearchAsync against temporary local folder feeds and a source with no resource
// providers (fails deterministically) — never the user's nuget.config or the network.
public sealed class NuGetPackageSearchServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("Invoklet-nuget-feeds").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CreateFeed(string name, params (string Id, string Version)[] packages)
    {
        var feed = Path.Combine(_root, name);
        Directory.CreateDirectory(feed);
        foreach (var (id, version) in packages)
        {
            using var zip = ZipFile.Open(Path.Combine(feed, $"{id}.{version}.nupkg"), ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry($"{id}.nuspec").Open());
            writer.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{id}</id>
                    <version>{version}</version>
                    <authors>test</authors>
                    <description>{id} test package</description>
                  </metadata>
                </package>
                """);
        }
        return feed;
    }

    private static SourceRepository Feed(string path) => Repository.Factory.GetCoreV3(path);

    // No resource providers at all: every GetResourceAsync returns null, so the source "fails".
    private static SourceRepository BrokenSource(string name)
        => new(new PackageSource($"https://user:secret@{name}.invalid/v3/index.json"), Array.Empty<INuGetResourceProvider>());

    [Fact]
    public async Task GetVersionsAsync_Should_MergeFeeds_Deduplicate_AndSortNewestFirst()
    {
        var a = CreateFeed("a", ("Demo.Package", "1.0.0"), ("Demo.Package", "2.0.0-beta"));
        var b = CreateFeed("b", ("Demo.Package", "1.0.0"), ("Demo.Package", "1.10.0"), ("Other.Package", "9.0.0"));
        var service = new NuGetPackageSearchService([Feed(a), Feed(b)]);

        var versions = await service.GetVersionsAsync("demo.package", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "2.0.0-beta", "1.10.0", "1.0.0" }, versions);
    }

    [Fact]
    public async Task GetVersionsAsync_Should_SucceedPartially_WhenOneSourceFails()
    {
        var a = CreateFeed("a", ("Demo.Package", "1.0.0"));
        var service = new NuGetPackageSearchService([BrokenSource("corp"), Feed(a)]);

        var versions = await service.GetVersionsAsync("Demo.Package", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "1.0.0" }, versions);
    }

    [Fact]
    public async Task GetVersionsAsync_Should_Throw_WithoutLeakingSourceUrls_WhenEverySourceFails()
    {
        var service = new NuGetPackageSearchService([BrokenSource("one"), BrokenSource("two")]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetVersionsAsync("Demo.Package", TestContext.Current.CancellationToken));

        Assert.DoesNotContain("secret", ex.Message);
        Assert.DoesNotContain("invalid", ex.Message);
    }

    [Fact]
    public async Task SearchAsync_Should_Throw_InsteadOfReportingNoPackages_WhenEverySourceFails()
    {
        var service = new NuGetPackageSearchService([BrokenSource("one")]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SearchAsync("Demo", new PackageSearchOptions(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetVersionsAsync_Should_HonorCancellation()
    {
        var a = CreateFeed("a", ("Demo.Package", "1.0.0"));
        var service = new NuGetPackageSearchService([Feed(a)]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetVersionsAsync("Demo.Package", cts.Token));
    }

    [Fact]
    public async Task GetVersionsAsync_Should_ReturnEmpty_ForAnUnknownPackage()
    {
        var a = CreateFeed("a", ("Demo.Package", "1.0.0"));
        var service = new NuGetPackageSearchService([Feed(a)]);

        Assert.Empty(await service.GetVersionsAsync("Missing.Package", TestContext.Current.CancellationToken));
    }

    // A feed package whose nuspec carries the given extra metadata elements.
    private string CreateDescribedFeed(string name, string id, string version, string metadata, string dependencies = "")
    {
        var feed = Path.Combine(_root, name);
        Directory.CreateDirectory(feed);
        using var zip = ZipFile.Open(Path.Combine(feed, $"{id}.{version}.nupkg"), ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry($"{id}.nuspec").Open());
        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <authors>test</authors>
                <description>{id} test package</description>
                {metadata}
                {(dependencies.Length == 0 ? "" : $"<dependencies>{dependencies}</dependencies>")}
              </metadata>
            </package>
            """);
        return feed;
    }

    [Fact]
    public async Task GetDetailsAsync_Should_ReadDependenciesLicenseAndProjectPage_OfTheChosenVersion()
    {
        var feed = CreateDescribedFeed("d", "Demo.Package", "2.0.0",
            "<projectUrl>https://demo.test/home</projectUrl><license type=\"expression\">MIT OR Apache-2.0</license>",
            "<group targetFramework=\"net8.0\"><dependency id=\"Dep.One\" version=\"1.2.0\" /></group>"
            + "<group targetFramework=\".NETStandard2.0\"><dependency id=\"Dep.Two\" version=\"[3.0.0, 4.0.0)\" /><dependency id=\"Dep.Three\" /></group>"
            + "<group targetFramework=\"net462\" />");
        CreateDescribedFeed("d", "Demo.Package", "1.0.0", "");
        var service = new NuGetPackageSearchService([Feed(feed)]);

        var details = await service.GetDetailsAsync("Demo.Package", "2.0.0", TestContext.Current.CancellationToken);

        Assert.NotNull(details);
        Assert.Equal("MIT OR Apache-2.0", details.License);
        Assert.Equal("https://demo.test/home", details.ProjectUrl);
        Assert.Equal(["net8.0", "netstandard2.0"], details.DependencyGroups.Select(g => g.TargetFramework)); // the empty group is left out
        Assert.Equal([("Dep.One", ">= 1.2.0")], details.DependencyGroups[0].Dependencies.Select(d => (d.Id, d.VersionRange)));
        Assert.Equal(["Dep.Two", "Dep.Three"], details.DependencyGroups[1].Dependencies.Select(d => d.Id));
        Assert.Equal([">= 3.0.0 && < 4.0.0", ""], details.DependencyGroups[1].Dependencies.Select(d => d.VersionRange));
        var older = await service.GetDetailsAsync("Demo.Package", "1.0.0", TestContext.Current.CancellationToken);
        Assert.Empty(older!.DependencyGroups);
        Assert.Null(older.License);
        Assert.Null(older.ProjectUrl);
    }

    [Fact]
    public async Task GetDetailsAsync_Should_UseTheNewestStableVersion_ForARangeOrAnUnlistedVersion()
    {
        var feed = CreateDescribedFeed("d", "Demo.Package", "1.0.0", "<projectUrl>https://demo.test/one</projectUrl>");
        CreateDescribedFeed("d", "Demo.Package", "2.0.0", "<projectUrl>https://demo.test/two</projectUrl>");
        CreateDescribedFeed("d", "Demo.Package", "3.0.0-beta", "<projectUrl>https://demo.test/beta</projectUrl>");
        var service = new NuGetPackageSearchService([Feed(feed)]);

        Assert.Equal("https://demo.test/two", (await service.GetDetailsAsync("Demo.Package", "[1.0.0, 5.0.0)", TestContext.Current.CancellationToken))!.ProjectUrl);
        Assert.Equal("https://demo.test/two", (await service.GetDetailsAsync("Demo.Package", null, TestContext.Current.CancellationToken))!.ProjectUrl);
        Assert.Equal("https://demo.test/two", (await service.GetDetailsAsync("Demo.Package", "9.9.9", TestContext.Current.CancellationToken))!.ProjectUrl);
        Assert.Equal("https://demo.test/beta", (await service.GetDetailsAsync("Demo.Package", "3.0.0-beta", TestContext.Current.CancellationToken))!.ProjectUrl);
    }

    [Fact]
    public async Task GetDetailsAsync_Should_DescribeALicenseFile_AndDropAMalformedOrNonWebAddress()
    {
        var file = CreateDescribedFeed("f", "Demo.File", "1.0.0", "<license type=\"file\">LICENSE.txt</license><projectUrl>javascript:alert(1)</projectUrl>");
        var bad = CreateDescribedFeed("b", "Demo.Bad", "1.0.0", "<projectUrl>not a url</projectUrl><licenseUrl>ftp://host/license.txt</licenseUrl>");
        var userinfo = CreateDescribedFeed("u", "Demo.User", "1.0.0", "<projectUrl>https://user:pass@host.test/</projectUrl>");
        var deprecated = CreateDescribedFeed("p", "Demo.Dep", "1.0.0", "<licenseUrl>https://aka.ms/deprecateLicenseUrl</licenseUrl>");
        var service = new NuGetPackageSearchService([Feed(file), Feed(bad), Feed(userinfo), Feed(deprecated)]);

        var fromFile = await service.GetDetailsAsync("Demo.File", "1.0.0", TestContext.Current.CancellationToken);
        Assert.Equal("License file included in the package", fromFile!.License);
        Assert.Null(fromFile.ProjectUrl);

        var fromBad = await service.GetDetailsAsync("Demo.Bad", "1.0.0", TestContext.Current.CancellationToken);
        Assert.Null(fromBad!.ProjectUrl);
        Assert.Null(fromBad.LicenseUrl);

        Assert.Null((await service.GetDetailsAsync("Demo.User", "1.0.0", TestContext.Current.CancellationToken))!.ProjectUrl);
        Assert.Null((await service.GetDetailsAsync("Demo.Dep", "1.0.0", TestContext.Current.CancellationToken))!.LicenseUrl);
    }

    [Fact]
    public async Task GetDetailsAsync_Should_ReturnNull_ForAnUnknownPackage_AndThrow_WhenEverySourceFails()
    {
        var a = CreateFeed("a", ("Demo.Package", "1.0.0"));

        Assert.Null(await new NuGetPackageSearchService([Feed(a)]).GetDetailsAsync("Missing.Package", "1.0.0", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new NuGetPackageSearchService([BrokenSource("one")]).GetDetailsAsync("Demo.Package", "1.0.0", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetDetailsAsync_Should_HonorCancellation()
    {
        var a = CreateFeed("a", ("Demo.Package", "1.0.0"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new NuGetPackageSearchService([Feed(a)]).GetDetailsAsync("Demo.Package", "1.0.0", cts.Token));
    }

    private static SourceRepository Feed(string path, string name) => Repository.Factory.GetCoreV3(new PackageSource(path, name));

    [Fact]
    public async Task SearchAsync_Should_QueryOnlyTheSelectedSource()
    {
        var a = CreateFeed("a", ("Demo.Alpha", "1.0.0"));
        var b = CreateFeed("b", ("Demo.Beta", "1.0.0"));
        var service = new NuGetPackageSearchService([Feed(a, "A"), Feed(b, "B")]);

        var all = await service.SearchAsync("Demo", new PackageSearchOptions(), TestContext.Current.CancellationToken);
        var onlyB = await service.SearchAsync("Demo", new PackageSearchOptions(Source: "b"), TestContext.Current.CancellationToken);

        Assert.Equal(["Demo.Alpha", "Demo.Beta"], all.Results.Select(r => r.Id).Order());
        Assert.Equal(["Demo.Beta"], onlyB.Results.Select(r => r.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync("Demo", new PackageSearchOptions(Source: "missing"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchAsync_Should_HidePrereleases_UnlessIncluded()
    {
        var a = CreateFeed("a", ("Demo.Package", "1.0.0"), ("Demo.Package", "2.0.0-beta"), ("Demo.Preview", "0.1.0-alpha"));
        var service = new NuGetPackageSearchService([Feed(a)]);

        var stable = await service.SearchAsync("Demo", new PackageSearchOptions(), TestContext.Current.CancellationToken);
        var withPrerelease = await service.SearchAsync("Demo", new PackageSearchOptions(IncludePrerelease: true),
            TestContext.Current.CancellationToken);

        Assert.Equal([("Demo.Package", "1.0.0")], stable.Results.Select(r => (r.Id, r.Version)));
        Assert.Contains(withPrerelease.Results, r => r is { Id: "Demo.Package", Version: "2.0.0-beta" });
        Assert.Contains(withPrerelease.Results, r => r.Id == "Demo.Preview");
    }

    [Fact]
    public async Task SearchAsync_Should_PageWithSkipAndTake_AndReportWhetherMoreMayFollow()
    {
        var a = CreateFeed("a", ("Demo.A", "1.0.0"), ("Demo.B", "1.0.0"), ("Demo.C", "1.0.0"));
        var service = new NuGetPackageSearchService([Feed(a)]);

        var first = await service.SearchAsync("Demo", new PackageSearchOptions(Take: 2), TestContext.Current.CancellationToken);
        var second = await service.SearchAsync("Demo", new PackageSearchOptions(Skip: 2, Take: 2), TestContext.Current.CancellationToken);

        Assert.Equal(2, first.Results.Count);
        Assert.True(first.HasMore);
        Assert.Single(second.Results);
        Assert.False(second.HasMore);
        Assert.Empty(first.Results.Select(r => r.Id).Intersect(second.Results.Select(r => r.Id)));
    }

    [Fact]
    public async Task StoreBackedService_Should_UseOnlyEnabledSources_AndReloadAfterTheStoreChanges()
    {
        var a = CreateFeed("a", ("Demo.Alpha", "1.0.0"));
        var b = CreateFeed("b", ("Demo.Beta", "1.0.0"));
        var store = new FakeSourceStore([new NuGetSource("A", a), new NuGetSource("B", b, IsEnabled: false)]);
        var service = new NuGetPackageSearchService(store);

        var before = await service.SearchAsync("Demo", new PackageSearchOptions(), TestContext.Current.CancellationToken);
        store.Save([new NuGetSource("B", b)]);
        var after = await service.SearchAsync("Demo", new PackageSearchOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(["Demo.Alpha"], before.Results.Select(r => r.Id));
        Assert.Equal(["Demo.Beta"], after.Results.Select(r => r.Id));
    }

    [Fact]
    public async Task SearchAsync_WithNoEnabledSource_Should_SayWhatToDo()
    {
        var service = new NuGetPackageSearchService(new FakeSourceStore([]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SearchAsync("Demo", new PackageSearchOptions(), TestContext.Current.CancellationToken));
        Assert.Contains("Configure package sources", ex.Message);
    }

    private sealed class FakeSourceStore(IReadOnlyList<NuGetSource> sources) : INuGetSourceStore
    {
        private IReadOnlyList<NuGetSource> _sources = sources;
        public string ConfigFilePath => "unused";
        public IReadOnlyList<NuGetSource> Load() => _sources;

        public void Save(IReadOnlyList<NuGetSource> sources)
        {
            _sources = sources;
            Changed?.Invoke();
        }

        public event Action? Changed;
    }
}
