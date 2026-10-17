using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Contracts.IntelliSense;
using RunPad.Debug;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.Compiler;
using RunPad.Languages.CSharp.IntelliSense;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Sdk;
using Xunit;
using DiagnosticSeverity = RunPad.Contracts.IntelliSense.DiagnosticSeverity;

namespace RunPad.Runtime.UI.Tests;

// A package can ship an assembly the framework has too (System.Text.Json, System.Memory, ...). The build
// keeps whichever has the higher assembly version; the editor used to reference both, so every type
// of that assembly was ambiguous there (CS0433) while the script compiled. Both sides now select the
// references the same way. A local folder feed with its own NuGet.Config: never the network or the
// user's package cache.
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
public sealed class NuGetFrameworkOverlapTests : IAsyncDisposable
{
    private const string Overlapping = "System.Text.Json";

    private readonly string _root = Directory.CreateTempSubdirectory("runpad-nuget-overlap").FullName;
    private readonly string _feed;
    private readonly string _config;
    private readonly string _tfm = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
    private int _dirs;

    public NuGetFrameworkOverlapTests()
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

    public ValueTask DisposeAsync() => TempDirectoryCleanup.DeleteAsync(_root);

    // Feed.Json ships its own System.Text.Json.dll, as System.Text.Json 8.0.x does on .NET 10; Feed.App
    // (its own assembly) depends on it, so it arrives transitively.
    private void PackOverlap(string assemblyVersion, string extraCode = "")
    {
        var json = Library(Overlapping, $$"""
            [assembly: System.Reflection.AssemblyVersion("{{assemblyVersion}}")]
            namespace System.Text.Json { public static class JsonSerializer { public static string Serialize<T>(T value) => ""; } {{extraCode}} }
            """);
        Pack("Feed.Json", "1.0.0", [json], []);
        Pack("Feed.App", "1.0.0", [Library("Feed.App", "namespace Feed { public static class App { public static int Answer => 42; } }")],
            [("Feed.Json", "1.0.0")]);
    }

    private string Library(string name, string code)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "build", (++_dirs).ToString())).FullName;
        return ScriptCompilerTests.CompileLibrary(dir, name, code, []);
    }

    private void Pack(string id, string version, string[] dlls, (string Id, string Version)[] dependencies)
    {
        var deps = string.Concat(dependencies.Select(d => $"""<dependency id="{d.Id}" version="{d.Version}" />"""));
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
        foreach (var dll in dlls)
            zip.CreateEntryFromFile(dll, $"lib/net8.0/{Path.GetFileName(dll)}");
    }

    private CompiledDotnetLanguageHostOptions Options(string output) => new()
    {
        Platform = new DotnetSdkProvider().Get().First(),
        ExecutionOptions = new ExecutionOptions
        {
            WorkingDirectory = output,
            Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
            Debugger = new DebuggerOptions { DebuggerPath = "" },
        },
        OutputDirectory = output,
        RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
        Kind = RpadKind.Statements,
        Packages = new Dictionary<string, string> { ["Feed.App"] = "1.0.0" },
        NuGetConfigFile = _config,
    };

    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance)
        {
            BackgroundAnalysisDebounce = TimeSpan.Zero,
        };
    }

    private static async Task<List<string>> ErrorsAsync(CSharpIntelliSenseService service, string code)
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
            var diagnostics = await result.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message).ToList();
        }
        finally
        {
            service.DiagnosticsChanged -= OnDiagnostics;
        }
    }

    // Builds the script (restoring Feed.App and Feed.Json from the feed), then points a fresh editor
    // service at that build's restore, as an open tab does after Run.
    private async Task<(CSharpIntelliSenseService Service, string Output)> BuildAndOpenAsync(string code)
    {
        var output = Directory.CreateDirectory(Path.Combine(_root, "script")).FullName;
        using (var compiler = new CSharpScriptCompiler(Options(output), NullLogger.Instance))
        {
            var build = await compiler.Compilation(code, TestContext.Current.CancellationToken);
            Assert.True(build.Success, string.Join("\n", build.Errors.Select(e => $"{e.Code} {e.Message}")));
        }

        var service = CreateService();
        service.UpdatePackageReferences(new Dictionary<string, Package> { ["Feed.App"] = new("1.0.0") },
            new PackageReferenceContext(_tfm, output, _config));
        return (service, output);
    }

    private static bool IsPackage(string path) => path.Contains($"{Path.DirectorySeparatorChar}feed.", StringComparison.OrdinalIgnoreCase);

    private static string? OverlappingReference(CSharpIntelliSenseService service) =>
        Assert.Single(service.ReferencePathsForTests, p => Path.GetFileName(p) == Overlapping + ".dll");

    // An older copy than the framework's: the build compiles against the framework's assembly
    // (JsonSerializerOptions.Web is newer than the package), and so does the editor now.
    [Fact]
    public async Task AnOlderPackageCopy_Should_GiveWayToTheFrameworksAssembly_InTheEditorToo()
    {
        PackOverlap("8.0.0.0");
        const string code = "var json = System.Text.Json.JsonSerializer.Serialize(Feed.App.Answer, System.Text.Json.JsonSerializerOptions.Web);\n";

        var (service, _) = await BuildAndOpenAsync(code);
        using (service)
        {
            Assert.Empty(await ErrorsAsync(service, code));
            Assert.False(IsPackage(OverlappingReference(service)!));
            // the transitive package assembly is still referenced; the overlapping one was resolved
            Assert.Contains(service.ReferencePathsForTests, p => Path.GetFileName(p) == "Feed.App.dll");
            Assert.Equal(["Feed.App.dll", Overlapping + ".dll"], service.PackageReferencePathsForTests.Select(Path.GetFileName).Order());
        }
    }

    // A newer copy than the framework's: the build uses the package's assembly and so does the editor;
    // every other framework reference stays, and removing the package brings the framework's back.
    [Fact]
    public async Task ANewerPackageCopy_Should_ReplaceOnlyThatFrameworkAssembly_UntilThePackageIsRemoved()
    {
        PackOverlap("99.0.0.0", "public static class PackageOnly { }");
        const string code = "var json = System.Text.Json.JsonSerializer.Serialize(Feed.App.Answer);\nvar t = typeof(System.Text.Json.PackageOnly);\n";
        int baseCount;
        using (var fresh = CreateService())
            baseCount = fresh.ReferencePathsForTests.Count;

        var (service, output) = await BuildAndOpenAsync(code);
        using (service)
        {
            Assert.Empty(await ErrorsAsync(service, code));
            Assert.True(IsPackage(OverlappingReference(service)!));
            // base references minus the displaced one, plus the two package assemblies
            Assert.Equal(baseCount + 1, service.ReferencePathsForTests.Count);

            service.UpdatePackageReferences(new Dictionary<string, Package>(), new PackageReferenceContext(_tfm, output, _config));

            Assert.False(IsPackage(OverlappingReference(service)!));
            Assert.Equal(baseCount, service.ReferencePathsForTests.Count);
            const string after = "var json = System.Text.Json.JsonSerializer.Serialize(1, System.Text.Json.JsonSerializerOptions.Web);\n";
            Assert.Empty(await ErrorsAsync(service, after));
            Assert.NotEmpty(await ErrorsAsync(service, "var t = typeof(System.Text.Json.PackageOnly);\n"));
        }
    }
}
