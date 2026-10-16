using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Contracts.IntelliSense;
using RunPad.Contracts.Models;
using RunPad.Debug;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.Compiler;
using RunPad.Languages.CSharp.IntelliSense;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Compiler;
using Xunit;
using DiagnosticSeverity = RunPad.Contracts.IntelliSense.DiagnosticSeverity;

namespace RunPad.Runtime.UI.Tests;

// The editor checks a script against the framework the script targets, as the build does — not
// against the one RunPad itself runs on. An older target is simulated with a "net9.0" targeting pack
// in a temporary package folder (the installed reference assemblies, with a System.Text.Json of its
// own that lacks newer APIs and has one of its own), so no SDK or pack has to be installed or
// downloaded; its own NuGet.Config keeps the user's package cache and the network out.
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
public sealed class TargetFrameworkIntelliSenseTests : IAsyncDisposable
{
    private const string OlderTarget = "net9.0";
    private const string OnlyInNewer = "var o = System.Text.Json.JsonSerializerOptions.Web;\n";
    private const string OnlyInOlder = "var t = typeof(System.Text.Json.OnlyInOlderTarget);\n";

    private readonly string _root = Directory.CreateTempSubdirectory("runpad-target-intellisense").FullName;
    private readonly string _packages;
    private readonly string _feed;
    private readonly string _config;
    private readonly string _currentTarget = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
    private int _dirs;

    public TargetFrameworkIntelliSenseTests()
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
        CreateOlderTargetingPack();
    }

    public ValueTask DisposeAsync() => TempDirectoryCleanup.DeleteAsync(_root);

    private void CreateOlderTargetingPack()
    {
        var current = TargetingPack.Find(_currentTarget, "dotnet", null);
        Assert.NotNull(current);
        var reference = Directory.CreateDirectory(Path.Combine(_packages, TargetingPack.PackageId.ToLowerInvariant(),
            "9.0.0", "ref", OlderTarget)).FullName;
        foreach (var dll in current.ReferenceAssemblies)
            File.Copy(dll, Path.Combine(reference, Path.GetFileName(dll)));
        File.Copy(Json("9.0.0.0", "public static class OnlyInOlderTarget { }"), Path.Combine(reference, "System.Text.Json.dll"), overwrite: true);
    }

    private string Json(string assemblyVersion, string extraCode = "") =>
        Library("System.Text.Json", $$"""
            [assembly: System.Reflection.AssemblyVersion("{{assemblyVersion}}")]
            namespace System.Text.Json { public static class JsonSerializer { public static string Serialize<T>(T value) => ""; } {{extraCode}} }
            """);

    private string Library(string name, string code)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "build", (++_dirs).ToString())).FullName;
        return ScriptCompilerTests.CompileLibrary(dir, name, code, []);
    }

    // A package that ships its own System.Text.Json.dll.
    private void PackJson(string assemblyVersion, string extraCode)
    {
        var dll = Json(assemblyVersion, extraCode);
        using var zip = ZipFile.Open(Path.Combine(_feed, "Feed.Json.1.0.0.nupkg"), ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("Feed.Json.nuspec").Open()))
            writer.Write("""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata><id>Feed.Json</id><version>1.0.0</version><authors>test</authors><description>Feed.Json</description></metadata>
                </package>
                """);
        zip.CreateEntryFromFile(dll, "lib/net8.0/System.Text.Json.dll");
    }

    private async Task<List<string>> BuildErrorsAsync(string target, string code, string output, IReadOnlyDictionary<string, string>? packages = null)
    {
        var options = new CompiledDotnetLanguageHostOptions
        {
            Platform = new DotnetSdk(target, target, target[3..] + ".0", target),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = output,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            OutputDirectory = output,
            RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
            Kind = RpadKind.Statements,
            Packages = packages ?? new Dictionary<string, string>(),
            NuGetConfigFile = _config,
        };
        using var compiler = new CSharpScriptCompiler(options, NullLogger.Instance);
        var result = await compiler.Compilation(code, TestContext.Current.CancellationToken);
        return result.Errors.Select(e => e.Code).ToList();
    }

    private static CSharpIntelliSenseService CreateService()
    {
        var referenceService = new CSharpReferenceService();
        var refs = referenceService.GetDefault().Append(referenceService.GetExtensionReference());
        return new CSharpIntelliSenseService(refs, NullLogger<CSharpIntelliSenseService>.Instance)
        {
            BackgroundAnalysisDebounce = TimeSpan.Zero,
        };
    }

    private static async Task<List<string>> EditorErrorsAsync(CSharpIntelliSenseService service, string code)
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

    private string Output(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private PackageReferenceContext Context(string target, string? output = null) => new(target, output, _config);

    private static string JsonReference(CSharpIntelliSenseService service) =>
        Assert.Single(service.ReferencePathsForTests, p => Path.GetFileName(p) == "System.Text.Json.dll");

    // The build itself: the older target lacks the newer API and has its own.
    [Fact]
    public async Task TheBuild_Should_CompileAgainstTheSelectedTarget()
    {
        Assert.NotEmpty(await BuildErrorsAsync(OlderTarget, OnlyInNewer, Output("a")));
        Assert.Empty(await BuildErrorsAsync(OlderTarget, OnlyInOlder, Output("b")));
        Assert.Empty(await BuildErrorsAsync(_currentTarget, OnlyInNewer, Output("c")));
        Assert.NotEmpty(await BuildErrorsAsync(_currentTarget, OnlyInOlder, Output("d")));
    }

    // Selecting the older SDK and back: the editor agrees with the build each time, keeps every other
    // reference (RunPad.Runtime's .Dump() included) and the reference count stays the same.
    [Fact]
    public async Task SwitchingTheTarget_AwayAndBack_Should_SwitchTheEditorsFrameworkReferences()
    {
        using var service = CreateService();
        var initial = service.ReferencePathsForTests.Count;

        service.UpdatePackageReferences(new Dictionary<string, Package>(), Context(OlderTarget));
        Assert.NotEmpty(await EditorErrorsAsync(service, OnlyInNewer));
        Assert.Empty(await EditorErrorsAsync(service, OnlyInOlder));
        Assert.Empty(await EditorErrorsAsync(service, "1.Dump();\nConsole.WriteLine(Environment.Version);\n"));
        Assert.StartsWith(_packages, JsonReference(service), StringComparison.Ordinal);
        Assert.Equal(initial, service.ReferencePathsForTests.Count);

        service.UpdatePackageReferences(new Dictionary<string, Package>(), Context(_currentTarget));
        Assert.Empty(await EditorErrorsAsync(service, OnlyInNewer + "1.Dump();\n"));
        Assert.NotEmpty(await EditorErrorsAsync(service, OnlyInOlder));
        Assert.Equal(initial, service.ReferencePathsForTests.Count);

        service.UpdatePackageReferences(new Dictionary<string, Package>(), Context(OlderTarget));
        Assert.NotEmpty(await EditorErrorsAsync(service, OnlyInNewer));
        Assert.Empty(await EditorErrorsAsync(service, OnlyInOlder));
    }

    // A target with no targeting pack anywhere keeps the references the editor already has.
    [Fact]
    public async Task ATargetWithoutAPack_Should_KeepTheCurrentReferences()
    {
        using var service = CreateService();
        var initial = service.ReferencePathsForTests.ToList();

        service.UpdatePackageReferences(new Dictionary<string, Package>(), Context("net5.0"));

        Assert.Equal(initial, service.ReferencePathsForTests);
        Assert.Empty(await EditorErrorsAsync(service, OnlyInNewer));
    }

    // A package's System.Text.Json between the two targets' versions: newer than the older target's,
    // so it replaces it there; older than the current one's, so it gives way there — in the build and
    // in the editor, after a restore for each target.
    [Fact]
    public async Task APackageOverlappingTheFramework_Should_BeResolvedAgainstTheSelectedTarget()
    {
        PackJson("9.5.0.0", "public static class FromPackage { }");
        var packages = new Dictionary<string, string> { ["Feed.Json"] = "1.0.0" };
        var editorPackages = new Dictionary<string, Package> { ["Feed.Json"] = new("1.0.0") };
        const string fromPackage = "var t = typeof(System.Text.Json.FromPackage);\n";
        using var service = CreateService();

        var older = Output("older");
        Assert.Empty(await BuildErrorsAsync(OlderTarget, fromPackage, older, packages));
        service.UpdatePackageReferences(editorPackages, Context(OlderTarget, older));
        Assert.Empty(await EditorErrorsAsync(service, fromPackage));
        Assert.NotEmpty(await EditorErrorsAsync(service, OnlyInOlder));
        Assert.StartsWith(Path.Combine(_packages, "feed.json"), JsonReference(service), StringComparison.Ordinal);

        var current = Output("current");
        Assert.NotEmpty(await BuildErrorsAsync(_currentTarget, fromPackage, current, packages));
        Assert.Empty(await BuildErrorsAsync(_currentTarget, OnlyInNewer, Output("current2"), packages));
        service.UpdatePackageReferences(editorPackages, Context(_currentTarget, current));
        Assert.NotEmpty(await EditorErrorsAsync(service, fromPackage));
        Assert.Empty(await EditorErrorsAsync(service, OnlyInNewer));
        Assert.DoesNotContain(_packages, JsonReference(service), StringComparison.Ordinal);
    }
}
