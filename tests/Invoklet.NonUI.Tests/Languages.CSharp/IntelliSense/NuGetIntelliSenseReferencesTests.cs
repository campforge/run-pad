using System.IO.Compression;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Invoklet.Languages.DotnetCommon.Compiler;
using Invoklet.NonUI.Tests.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MetadataReference = Microsoft.CodeAnalysis.MetadataReference;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense;

// IntelliSense sees the same package assemblies as the build: after a restore, the versions and
// transitive packages it resolved; before one, the local package folder chosen with NuGet's version
// rules — "latest", floating versions and ranges included, which used to find nothing because the
// version text was used as a folder name. Everything runs against a temporary package folder and a
// local folder feed with their own NuGet.Config: never the network or the user's package cache.
public sealed class NuGetIntelliSenseReferencesTests : IAsyncDisposable
{
    private const string Tfm = "net8.0";

    private readonly string _root = Directory.CreateTempSubdirectory("Invoklet-nuget-intellisense").FullName;
    private readonly string _packages;
    private readonly string _feed;
    private readonly string _config;
    private int _builds;

    public NuGetIntelliSenseReferencesTests()
    {
        _packages = Directory.CreateDirectory(Path.Combine(_root, "packages")).FullName;
        _feed = Directory.CreateDirectory(Path.Combine(_root, "feed")).FullName;
        _config = Path.Combine(_root, "NuGet.Config");
        File.WriteAllText(_config, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config><add key="globalPackagesFolder" value="{_packages}" /></config>
              <packageSources>
                <clear />
                <add key="local" value="{_feed}" />
              </packageSources>
            </configuration>
            """);
    }

    public ValueTask DisposeAsync() => TempDirectoryCleanup.DeleteAsync(_root);

    private string Library(string assemblyName, string code, params string[] references)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "build", (++_builds).ToString())).FullName;
        // The package's lib/net8.0 assets must reference the same framework as the consumer.
        // CI can have a real .NET 8 pack even when the test runner itself uses .NET 10.
        var framework = TargetingPack.Find(Tfm, "dotnet", null)?.ReferenceAssemblies
            ?? new CSharpReferenceService().GetDefault();
        var metadata = framework.Concat(references).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(code)], metadata,
            new CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        var path = Path.Combine(dir, assemblyName + ".dll");
        var emit = compilation.Emit(path);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return path;
    }

    // An already extracted package in the global packages folder, as NuGet leaves it.
    private string Extracted(string id, string version, params (string Folder, string Code)[] assets)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_packages, id.ToLowerInvariant(), version.ToLowerInvariant())).FullName;
        foreach (var (folder, code) in assets)
        {
            var target = Directory.CreateDirectory(Path.Combine(dir, folder.Replace('/', Path.DirectorySeparatorChar))).FullName;
            File.Copy(Library(id, code), Path.Combine(target, id + ".dll"));
        }
        File.WriteAllText(Path.Combine(dir, ".nupkg.metadata"), "{}");
        return dir;
    }

    private static string Lib(string version) =>
        $"namespace Feed {{ public static class Info {{ public static string Version => \"{version}\"; }} }}";

    // A .nupkg in the local feed, for a real restore.
    private void Pack(string id, string version, string code, string[] references, (string Id, string Version)[]? dependencies = null)
    {
        var dll = Library(id, code, references);
        var deps = string.Concat((dependencies ?? []).Select(d => $"""<dependency id="{d.Id}" version="{d.Version}" />"""));
        using var zip = ZipFile.Open(Path.Combine(_feed, $"{id}.{version}.nupkg"), ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry($"{id}.nuspec").Open()))
            writer.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{id}</id>
                    <version>{version}</version>
                    <authors>test</authors>
                    <description>{id}</description>
                    <dependencies><group targetFramework="{Tfm}">{deps}</group></dependencies>
                  </metadata>
                </package>
                """);
        zip.CreateEntryFromFile(dll, $"lib/{Tfm}/{id}.dll");
    }

    private IReadOnlyList<string> Resolve(string id, string version, string tfm = Tfm) =>
        NuGetRestorer.CompileReferencesWithoutRestore(new Dictionary<string, string> { [id] = version }, tfm,
            objDirectory: null, _config);

    private string VersionOf(string path) => Path.GetRelativePath(_packages, path).Split(Path.DirectorySeparatorChar)[1];

    // ── Before any restore: the local package folder ─────────────────────────

    [Theory]
    [InlineData("latest", "2.0.0")]        // newest stable, not the newer prerelease
    [InlineData("*", "2.0.0")]
    [InlineData("", "2.0.0")]              // an empty version means latest, as in a restore
    [InlineData("3.0.0-*", "3.0.0-beta")]  // a prerelease float may pick a prerelease
    [InlineData("1.0.0", "1.0.0")]         // pinned: itself, not something newer
    [InlineData("1.*", "1.5.0")]           // floating within 1.x
    [InlineData("[1.0.0,2.0.0)", "1.0.0")] // a range prefers its lowest version, like restore
    [InlineData("(1.0.0,2.0.0)", "1.5.0")] // exclusive bounds are kept
    [InlineData("(1.0.0,2.0.0]", "1.5.0")]
    [InlineData("[1.2.0,1.9.0]", "1.5.0")]
    public void BeforeRestore_TheVersion_Should_FollowNuGetsRules(string range, string expected)
    {
        foreach (var version in new[] { "1.0.0", "1.5.0", "2.0.0", "3.0.0-beta" })
            Extracted("Feed.Lib", version, ($"lib/{Tfm}", Lib(version)));

        var reference = Assert.Single(Resolve("Feed.Lib", range));

        Assert.Equal(expected, VersionOf(reference));
    }

    [Theory]
    [InlineData("[5.0.0,)")]       // nothing local satisfies it
    [InlineData("(1.0.0,1.5.0)")]  // nothing between the exclusive bounds
    [InlineData("not-a-version")]  // invalid: no references, no exception
    [InlineData("[2.0.0,1.0.0]")]
    public void BeforeRestore_AnUnsatisfiableOrInvalidVersion_Should_GiveNothing(string range)
    {
        Extracted("Feed.Lib", "1.0.0", ($"lib/{Tfm}", Lib("1.0.0")));
        Extracted("Feed.Lib", "1.5.0", ($"lib/{Tfm}", Lib("1.5.0")));

        Assert.Empty(Resolve("Feed.Lib", range));
        Assert.Empty(Resolve("Feed.Missing", "latest"));
    }

    // NuGet writes .nupkg.metadata last: a folder without it is still being extracted (or broken).
    [Fact]
    public void BeforeRestore_AnIncompleteExtraction_Should_BeIgnored()
    {
        Extracted("Feed.Lib", "1.0.0", ($"lib/{Tfm}", Lib("1.0.0")));
        var partial = Extracted("Feed.Lib", "2.0.0", ($"lib/{Tfm}", Lib("2.0.0")));
        File.Delete(Path.Combine(partial, ".nupkg.metadata"));

        Assert.Equal("1.0.0", VersionOf(Assert.Single(Resolve("Feed.Lib", "latest"))));
    }

    // Compile assets as a build selects them: ref/ before lib/, and the nearest framework.
    [Theory]
    [InlineData("net8.0", "ref/net8.0")]
    [InlineData("net9.0", "ref/net8.0")]
    [InlineData("net7.0", "lib/net6.0")]
    [InlineData("net6.0", "lib/net6.0")]
    public void BeforeRestore_TheCompileAssets_Should_BeTheBuildsOnes(string tfm, string expectedFolder)
    {
        var dir = Extracted("Feed.Assets", "1.0.0",
            ("ref/net8.0", Lib("ref8")), ("lib/net8.0", Lib("lib8")), ("lib/net6.0", Lib("lib6")),
            ("lib/netstandard2.0", Lib("ns20")));

        var reference = Assert.Single(Resolve("Feed.Assets", "1.0.0", tfm));

        Assert.Equal(Path.Combine(dir, expectedFolder.Replace('/', Path.DirectorySeparatorChar), "Feed.Assets.dll"), reference);
    }

    // ── After a restore: its own selection ───────────────────────────────────

    private async Task<string> RestoreAsync(IReadOnlyDictionary<string, string> packages, string objDirectory)
    {
        var restored = await NuGetRestorer.RestoreAsync(packages, Tfm, "cs", objDirectory, _config,
            TestContext.Current.CancellationToken);
        Assert.Empty(restored.Errors);
        return objDirectory;
    }

    [Fact]
    public async Task AfterRestore_TheRestoredVersions_AndTransitivePackages_Should_BeUsed()
    {
        var core = Library("Feed.Core", "namespace Feed { public static class Core { } }");
        Pack("Feed.Core", "1.0.0", "namespace Feed { public static class Core { } }", []);
        Pack("Feed.App", "1.0.0", "namespace Feed { public static class App1 { } }", [core], [("Feed.Core", "1.0.0")]);
        Pack("Feed.App", "2.0.0", "namespace Feed { public static class App2 { } }", [core], [("Feed.Core", "1.0.0")]);
        var packages = new Dictionary<string, string> { ["Feed.App"] = "latest" };
        var obj = await RestoreAsync(packages, Path.Combine(_root, "out", "obj"));

        var references = NuGetRestorer.CompileReferencesWithoutRestore(packages, Tfm, obj, _config);

        Assert.Equal(["Feed.App.dll@2.0.0", "Feed.Core.dll@1.0.0"],
            references.Select(r => $"{Path.GetFileName(r)}@{VersionOf(r)}").Order());
    }

    // A restore made for other packages (or another framework) does not describe the script any more.
    [Fact]
    public async Task AfterTheDependenciesChange_TheOldRestore_Should_NotBeUsed()
    {
        Pack("Feed.Core", "1.0.0", "namespace Feed { public static class Core { } }", []);
        Pack("Feed.Other", "1.0.0", "namespace Feed { public static class Other { } }", []);
        var obj = await RestoreAsync(new Dictionary<string, string> { ["Feed.Core"] = "1.0.0", ["Feed.Other"] = "1.0.0" },
            Path.Combine(_root, "out", "obj"));
        var now = new Dictionary<string, string> { ["Feed.Other"] = "1.0.0" };

        Assert.Equal(["Feed.Other.dll"], NuGetRestorer.CompileReferencesWithoutRestore(now, Tfm, obj, _config).Select(Path.GetFileName));
        Assert.Null(NuGetRestorer.ReadRestored(now, Tfm, obj, _config));
        Assert.Null(NuGetRestorer.ReadRestored(
            new Dictionary<string, string> { ["Feed.Core"] = "1.0.0", ["Feed.Other"] = "1.0.0" }, "net9.0", obj, _config));
    }

    // ── Through the real C# IntelliSense service ─────────────────────────────

    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance)
        {
            BackgroundAnalysisDebounce = TimeSpan.Zero,
        };
    }

    private static async Task<IReadOnlyList<IDiagnosticInfo>> DiagnosticsAsync(CSharpIntelliSenseService service, string code)
    {
        var result = new TaskCompletionSource<IReadOnlyList<IDiagnosticInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDiagnostics(string analyzed, IReadOnlyList<IDiagnosticInfo> diagnostics)
        {
            if (analyzed == code) result.TrySetResult(diagnostics);
        }
        service.DiagnosticsChanged += OnDiagnostics;
        try
        {
            service.NotifyTextChanged(code);
            return await result.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally
        {
            service.DiagnosticsChanged -= OnDiagnostics;
        }
    }

    private static IReadOnlyList<IDiagnosticInfo> Errors(IReadOnlyList<IDiagnosticInfo> diagnostics) =>
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    private PackageReferenceContext Context(string? outputDirectory = null) => new(Tfm, outputDirectory, _config);

    // The reported case: "latest" compiled and ran, but completion and diagnostics lost the package.
    [Fact]
    public async Task ALatestPackage_Should_BeVisibleToCompletion_AndDiagnostics()
    {
        Extracted("Feed.Greeter", "1.2.0", ($"lib/{Tfm}",
            "namespace Feed.Greeting { public static class Greeter { public static string Hi() => \"hi\"; } }"));
        using var service = CreateService();
        service.UpdatePackageReferences(new Dictionary<string, Package> { ["Feed.Greeter"] = new("latest") }, Context());

        const string code = "var s = Feed.Greeting.Greeter.";
        var completions = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(completions);
        Assert.Contains(completions.Items, i => i.DisplayText == "Hi");

        Assert.Empty(Errors(await DiagnosticsAsync(service, "var s = Feed.Greeting.Greeter.Hi();")));

        // auto-import: the type is offered with the using that brings it in
        const string unimported = "Greet";
        var importable = await service.GetCompletionsAsync(unimported, unimported.Length, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(importable);
        Assert.Contains(importable.Items, i => i.DisplayText == "Greeter" && i.UsingsToAdd.Contains("Feed.Greeting"));
    }

    // After a build restores a different version (here: the range now resolves to 2.0.0 on the feed),
    // the same tab's service switches to it and drops the old one; removing the package removes it.
    [Fact]
    public async Task AfterARestore_TheService_Should_SwitchToTheResolvedVersion_AndDropStaleOnes()
    {
        Extracted("Feed.Versioned", "1.0.0", ($"lib/{Tfm}", "namespace Feed { public static class V1 { } }"));
        using var service = CreateService();
        var output = Directory.CreateDirectory(Path.Combine(_root, "script")).FullName;
        var packages = new Dictionary<string, Package> { ["Feed.Versioned"] = new("[1.0.0,3.0.0)") };

        service.UpdatePackageReferences(packages, Context(output));
        Assert.Empty(Errors(await DiagnosticsAsync(service, "var t = typeof(Feed.V1);")));

        // The feed has 2.0.0 and 1.0.0 is gone from it: restore resolves the range to 2.0.0.
        Pack("Feed.Versioned", "2.0.0", "namespace Feed { public static class V2 { } }", []);
        await RestoreAsync(new Dictionary<string, string> { ["Feed.Versioned"] = "[2.0.0,3.0.0)" },
            RoslynScriptCompiler.ObjDirectory(output));
        packages["Feed.Versioned"] = new("[2.0.0,3.0.0)");
        service.UpdatePackageReferences(packages, Context(output));

        Assert.Equal("2.0.0", VersionOf(Assert.Single(service.PackageReferencePathsForTests)));
        Assert.Empty(Errors(await DiagnosticsAsync(service, "var t = typeof(Feed.V2);")));
        Assert.NotEmpty(Errors(await DiagnosticsAsync(service, "var u = typeof(Feed.V1);")));

        service.UpdatePackageReferences(new Dictionary<string, Package>(), Context(output));
        Assert.Empty(service.PackageReferencePathsForTests);
        Assert.NotEmpty(Errors(await DiagnosticsAsync(service, "var w = typeof(Feed.V2);")));
    }
}
