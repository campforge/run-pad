using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.Sdk;
using RunPad.Languages.DotnetCommon.Compiler;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Restore must use RunPad's own NuGet.Config (RunPadNuGetConfigStore) — the same sources the
// Dependencies window searches. Real dotnet build against a local folder feed holding a package that
// exists nowhere else: it only restores when the generated project points RestoreConfigFile at the
// config. The config's globalPackagesFolder is a temp folder, so nothing reaches the user's cache.
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
public sealed class MsBuildProcessCompilerNuGetConfigTests : IDisposable
{
    private const string PackageId = "RunPad.Test.ConfigOnlyPackage";
    private readonly string _root = Directory.CreateTempSubdirectory("runpad-restore-config").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CreateConfigWithLocalFeed()
    {
        var feed = Directory.CreateDirectory(Path.Combine(_root, "feed")).FullName;
        using (var zip = ZipFile.Open(Path.Combine(feed, $"{PackageId}.1.0.0.nupkg"), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry($"{PackageId}.nuspec").Open()))
        {
            writer.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{PackageId}</id>
                    <version>1.0.0</version>
                    <authors>test</authors>
                    <description>Only in a local test feed.</description>
                  </metadata>
                </package>
                """);
        }

        var config = Path.Combine(_root, "NuGet.Config");
        File.WriteAllText(config, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config><add key="globalPackagesFolder" value="{Path.Combine(_root, "packages")}" /></config>
              <packageSources>
                <clear />
                <add key="local" value="{feed}" />
              </packageSources>
            </configuration>
            """);
        return config;
    }

    private MsBuildProcessCompiler CreateCompiler(string outputDir, string? nugetConfigFile) => new(
        CSharpProjectProfile.Instance,
        sdk: new DotnetSdkProvider().Get().First(),
        executionOptions: new ExecutionOptions
        {
            WorkingDirectory = outputDir,
            Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
            Debugger = new DebuggerOptions { DebuggerPath = "" },
        },
        outputDir: outputDir,
        runPadRuntimeLib: new CSharpReferenceService().GetExtensionReference(),
        outputName: "Query",
        nugetPackages: new Dictionary<string, string> { [PackageId] = "1.0.0" },
        usings: [],
        assemblyReferences: [],
        projectReferences: [],
        logger: NullLogger<MsBuildProcessCompiler>.Instance,
        nugetConfigFile: nugetConfigFile);

    [Fact]
    public async Task Restore_Should_UseTheGivenNuGetConfig()
    {
        var config = CreateConfigWithLocalFeed();
        var outputDir = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        using var compiler = CreateCompiler(outputDir, config);

        var result = await compiler.Compilation("System.Console.WriteLine(1);", CancellationToken.None);

        Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));
        Assert.True(Directory.Exists(Path.Combine(_root, "packages", PackageId.ToLowerInvariant())));
    }
}
