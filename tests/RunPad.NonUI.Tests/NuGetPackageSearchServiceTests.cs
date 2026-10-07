using System.IO.Compression;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using RunPad.Languages.CSharp;
using Xunit;

namespace RunPad.NonUI.Tests;

// GetVersionsAsync/SearchAsync against temporary local folder feeds and a source with no resource
// providers (fails deterministically) — never the user's nuget.config or the network.
public sealed class NuGetPackageSearchServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("runpad-nuget-feeds").FullName;

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
            () => service.SearchAsync("Demo", TestContext.Current.CancellationToken));
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
}
