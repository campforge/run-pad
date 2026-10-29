using System.IO.Compression;
using System.Runtime.InteropServices;
using Invoklet.Build.Core.Models;
using Invoklet.Format.RPad;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.Compiler;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;
using Invoklet.Languages.DotnetCommon.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// In-process NuGet restore for scripts (NuGetRestorer, no MSBuild) against a local folder feed built by
// the test, with its own NuGet.Config whose globalPackagesFolder is a temp folder — never the network,
// the user's NuGet settings or their package cache.
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
public sealed class ScriptPackageRestoreTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("Invoklet-restore").FullName;
    private readonly string _feed;
    private readonly string _config;
    private int _dirs;

    public ScriptPackageRestoreTests()
    {
        _feed = Directory.CreateDirectory(Path.Combine(_root, "feed")).FullName;
        _config = Path.Combine(_root, "NuGet.Config");
        File.WriteAllText(_config, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config><add key="globalPackagesFolder" value="{Path.Combine(_root, "packages")}" /></config>
              <packageSources>
                <clear />
                <add key="local" value="{_feed}" />
              </packageSources>
            </configuration>
            """);
    }

    // Bounded retry for a briefly locked Feed.Greeter.dll on Windows — see TempDirectoryCleanup.
    public ValueTask DisposeAsync() => TempDirectoryCleanup.DeleteAsync(_root);

    private CompiledDotnetLanguageHostOptions Options(IReadOnlyDictionary<string, string> packages, string? outputDirectory = null)
    {
        var dir = outputDirectory ?? Directory.CreateDirectory(Path.Combine(_root, $"out{++_dirs}")).FullName;
        return new CompiledDotnetLanguageHostOptions
        {
            Platform = new DotnetSdkProvider().Get().First(),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = dir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            OutputDirectory = dir,
            RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
            Kind = RpadKind.Statements,
            Packages = packages,
            NuGetConfigFile = _config,
        };
    }

    // A package with a real lib/net8.0 assembly, optional dependencies and extra files (e.g. runtimes/).
    private void Pack(string id, string version, string libCode, string[] libReferences,
        (string Id, string Version)[]? dependencies = null, (string Path, string Content)[]? files = null)
    {
        var buildDir = Directory.CreateDirectory(Path.Combine(_root, "build", id, version)).FullName;
        var dll = ScriptCompilerTests.CompileLibrary(buildDir, id, libCode, libReferences);
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
                    <dependencies><group targetFramework="net8.0">{deps}</group></dependencies>
                  </metadata>
                </package>
                """);
        zip.CreateEntryFromFile(dll, $"lib/net8.0/{id}.dll");
        foreach (var (path, content) in files ?? [])
            using (var writer = new StreamWriter(zip.CreateEntry(path).Open()))
                writer.Write(content);
    }

    private static async Task<List<string>> RunAsync(CompiledDotnetLanguageHostOptions options, string code)
    {
        await using var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance);
        var output = new List<string>();
        await host.RunAsync(code, (data, kind) => { lock (output) output.Add($"{kind}: {data}"); },
            TestContext.Current.CancellationToken);
        return output;
    }

    [Fact]
    public async Task Package_Should_BeCompiledAgainst_AndShipWithTheScript()
    {
        Pack("Feed.Greeter", "1.0.0", "public static class Greeter { public static string Hi() => \"hi from package\"; }", []);
        var options = Options(new Dictionary<string, string> { ["Feed.Greeter"] = "1.0.0" });

        var output = await RunAsync(options, "Console.WriteLine(Greeter.Hi());");

        Assert.Contains("StandardOutput: hi from package", output);
        Assert.True(File.Exists(Path.Combine(options.OutputDirectory, "bin", "Feed.Greeter.dll")));
        Assert.True(File.Exists(Path.Combine(options.OutputDirectory, "obj", "project.assets.json")));
        Assert.True(Directory.Exists(Path.Combine(_root, "packages", "feed.greeter", "1.0.0")));
    }

    // "latest" (Invoklet's word for NuGet's floating "*") picks the newest stable version.
    [Fact]
    public async Task Latest_Should_ResolveToTheNewestVersion()
    {
        Pack("Feed.Versioned", "1.0.0", "public static class V { public static string Value => \"one\"; }", []);
        Pack("Feed.Versioned", "2.0.0", "public static class V { public static string Value => \"two\"; }", []);

        var output = await RunAsync(Options(new Dictionary<string, string> { ["Feed.Versioned"] = "latest" }),
            "Console.WriteLine(V.Value);");

        Assert.Contains("StandardOutput: two", output);
    }

    [Fact]
    public async Task TransitiveDependencies_Should_BeRestoredAndShipped()
    {
        var buildDir = Directory.CreateDirectory(Path.Combine(_root, "deps")).FullName;
        Pack("Feed.Core", "1.0.0", "public static class Core { public static string Name => \"core\"; }", []);
        var core = ScriptCompilerTests.CompileLibrary(buildDir, "Feed.Core", "public static class Core { public static string Name => \"core\"; }", []);
        Pack("Feed.App", "1.0.0", "public static class App { public static string Describe() => \"app on \" + Core.Name; }", [core],
            dependencies: [("Feed.Core", "1.0.0")]);
        var options = Options(new Dictionary<string, string> { ["Feed.App"] = "1.0.0" });

        var output = await RunAsync(options, "Console.WriteLine(App.Describe());");

        Assert.Contains("StandardOutput: app on core", output);
        Assert.True(File.Exists(Path.Combine(options.OutputDirectory, "bin", "Feed.Core.dll")));
    }

    // runtimes/<rid>/native assets of the current platform are placed next to the script (e.g. SQLite's
    // e_sqlite3), like a RID-specific build.
    [Fact]
    public async Task NativeAssets_OfTheCurrentPlatform_Should_BeCopied()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        Pack("Feed.Native", "1.0.0", "public static class N { }", [],
            files: [($"runtimes/{rid}/native/feed-native.bin", "native"), ("runtimes/other-os-x64/native/other.bin", "other")]);
        var options = Options(new Dictionary<string, string> { ["Feed.Native"] = "1.0.0" });
        using var compiler = new CSharpScriptCompiler(options, NullLogger.Instance);

        var result = await compiler.Compilation("Console.WriteLine(1);", TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join("\n", result.Errors.Select(e => e.Message)));
        Assert.True(File.Exists(Path.Combine(options.OutputDirectory, "bin", "feed-native.bin")));
        Assert.False(File.Exists(Path.Combine(options.OutputDirectory, "bin", "other.bin")));
    }

    // Packages ship native assets for portable RIDs (linux-x64), while a .NET built by a Linux
    // distribution reports its own (ubuntu.24.04-x64): the portable folder is used too.
    [Fact]
    public async Task NativeAssets_OfThePortableRid_Should_BeCopied()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var portable = $"{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        Pack("Feed.Portable", "1.0.0", "public static class P { }", [], files: [($"runtimes/{portable}/native/portable.bin", "native")]);
        var options = Options(new Dictionary<string, string> { ["Feed.Portable"] = "1.0.0" });
        using var compiler = new CSharpScriptCompiler(options, NullLogger.Instance);

        var result = await compiler.Compilation("Console.WriteLine(1);", TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join("\n", result.Errors.Select(e => e.Message)));
        Assert.True(File.Exists(Path.Combine(options.OutputDirectory, "bin", "portable.bin")));
    }

    [Fact]
    public void ADistributionsRid_Should_FallBackToThePortableOne()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.Equal(["ubuntu.24.04-x64", "linux-x64", "ubuntu.24.04", "linux", "unix", "any"],
            NuGetRestorer.RuntimeIdentifierFallbacks("ubuntu.24.04-x64", Architecture.X64));
        Assert.Equal(["alpine.3.20-arm64", "linux-musl-arm64", "alpine.3.20", "linux-musl", "linux", "unix", "any"],
            NuGetRestorer.RuntimeIdentifierFallbacks("alpine.3.20-arm64", Architecture.Arm64));
        Assert.Equal(["linux-x64", "linux", "unix", "any"], NuGetRestorer.RuntimeIdentifierFallbacks("linux-x64", Architecture.X64));
    }

    [Theory]
    [InlineData("Feed.DoesNotExist", "1.0.0", "NU1101")]
    [InlineData("Feed.Greeter", "not-a-real-version", "NU1008")]
    public async Task RestoreProblems_Should_BeReportedAsErrors(string id, string version, string code)
    {
        Pack("Feed.Greeter", "1.0.0", "public static class Greeter { }", []);
        using var compiler = new CSharpScriptCompiler(Options(new Dictionary<string, string> { [id] = version }), NullLogger.Instance);

        var result = await compiler.Compilation("Console.WriteLine(1);", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Code == code);
    }

    // Restore runs only when the package set changes — editing the script reuses the lock file.
    [Fact]
    public async Task EditingTheScript_Should_NotRestoreAgain_ButChangingPackages_Should()
    {
        Pack("Feed.Greeter", "1.0.0", "public static class Greeter { public static string Hi() => \"hi\"; }", []);
        Pack("Feed.Versioned", "1.0.0", "public static class V { }", []);
        var dir = Directory.CreateDirectory(Path.Combine(_root, "incremental")).FullName;
        var assets = Path.Combine(dir, "obj", "project.assets.json");

        async Task Compile(Dictionary<string, string> packages, string code)
        {
            using var compiler = new CSharpScriptCompiler(Options(packages, dir), NullLogger.Instance);
            var result = await compiler.Compilation(code, TestContext.Current.CancellationToken);
            Assert.True(result.Success, string.Join("\n", result.Errors.Select(e => e.Message)));
        }

        await Compile(new() { ["Feed.Greeter"] = "1.0.0" }, "Console.WriteLine(Greeter.Hi());");
        var first = File.GetLastWriteTimeUtc(assets);
        await Compile(new() { ["Feed.Greeter"] = "1.0.0" }, "Console.WriteLine(Greeter.Hi() + 1);");
        Assert.Equal(first, File.GetLastWriteTimeUtc(assets));

        await Compile(new() { ["Feed.Greeter"] = "1.0.0", ["Feed.Versioned"] = "1.0.0" }, "Console.WriteLine(Greeter.Hi());");
        Assert.NotEqual(first, File.GetLastWriteTimeUtc(assets));
    }
}
