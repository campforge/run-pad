using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.Sdk;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Compiler;
using RunPad.Languages.VB;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// R6 (docs/handoffs/project-reference-build.md): projects referenced from Dependencies/References
// must be built together with the script — library source, project file and transitive reference
// changes have to reach the next run even when the script text itself is unchanged, and a broken
// library must fail the build instead of rerunning the previous Query.dll. Real `dotnet build` +
// real execution of the produced binary: the bug is entirely about MSBuild's dependency graph and
// what actually ends up in bin/. Every fixture lives in its own temp directory.
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
public class MsBuildProcessCompilerProjectReferenceTests : IDisposable
{
    // Not "Lib" — that's a reserved keyword in VB.
    private const string CSharpScript = "System.Console.WriteLine(Fixture.Greeter.Value());";

    private const string VbScript =
        "Module Program\nSub Main()\nConsole.WriteLine(Fixture.Greeter.Value())\nEnd Sub\nEnd Module\n";

    private static readonly TimeSpan CompileTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(1);

    private readonly string _root = Directory.CreateTempSubdirectory("runpad-project-ref-test").FullName;
    private readonly string _outputDir;
    private readonly string _tfm = new DotnetSdkProvider().Get().First().TargetFrameworkMoniker;

    public MsBuildProcessCompilerProjectReferenceTests()
    {
        _outputDir = Path.Combine(_root, "script");
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private MsBuildProcessCompiler CreateCompiler(DotnetLanguageProjectProfile profile, params string[] projectReferences)
    {
        var sdk = new DotnetSdkProvider().Get().First();
        return new MsBuildProcessCompiler(
            profile,
            sdk: sdk,
            executionOptions: new ExecutionOptions
            {
                WorkingDirectory = _outputDir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            outputDir: _outputDir,
            runPadRuntimeLib: new CSharpReferenceService().GetExtensionReference(),
            outputName: "Query",
            nugetPackages: new Dictionary<string, string>(),
            usings: [],
            assemblyReferences: [],
            projectReferences: projectReferences,
            logger: NullLogger<MsBuildProcessCompiler>.Instance);
    }

    private string WriteProject(string name, string source, string extraProjectXml = "")
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.cs"), source);
        var projectPath = Path.Combine(dir, $"{name}.csproj");
        File.WriteAllText(projectPath, LibraryProjectXml(extraProjectXml));
        return projectPath;
    }

    private string LibraryProjectXml(string extraProjectXml) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
            <PropertyGroup>
                <TargetFramework>{_tfm}</TargetFramework>
            </PropertyGroup>
            {extraProjectXml}
        </Project>
        """;

    private static string GreeterSource(string body) =>
        $"namespace Fixture; public static class Greeter {{ public static string Value() {{\n{body}\n}} }}";

    private static async Task<string> RunAsync(string outputDll)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", $"\"{outputDll}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        using var timeout = new CancellationTokenSource(RunTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{outputDll} did not finish within {RunTimeout}.");
        }

        Assert.True(process.ExitCode == 0, $"exit code {process.ExitCode}: {await stderr}");
        return (await stdout).Trim();
    }

    // Bounded so a regression of the R6 review hang (a build node holding the redirected output
    // open after `dotnet build` exits) fails the test instead of blocking the run for 10+ minutes.
    private static async Task<ProcessCompilerResult> CompileAsync(MsBuildProcessCompiler compiler, string code)
    {
        using var timeout = new CancellationTokenSource(CompileTimeout);
        try
        {
            return await compiler.Compilation(code, timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            compiler.Dispose(); // kills the build's process tree
            throw new TimeoutException($"Compilation did not finish within {CompileTimeout}.");
        }
    }

    private static async Task<string> CompileAndRunAsync(MsBuildProcessCompiler compiler, string code)
    {
        var result = await CompileAsync(compiler, code);
        Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));
        return await RunAsync(result.OutputBinaryFile);
    }

    [Fact]
    public async Task Compilation_Should_BuildAnUnbuiltReferencedProject_AndPickUpItsSourceChanges()
    {
        var libSource = Path.Combine(_root, "Lib", "Lib.cs");
        var lib = WriteProject("Lib", GreeterSource("return \"v1\";"));
        using var compiler = CreateCompiler(CSharpProjectProfile.Instance, lib);

        Assert.Equal("v1", await CompileAndRunAsync(compiler, CSharpScript));

        // Script text unchanged — only the library's own source moves.
        File.WriteAllText(libSource, GreeterSource("return \"v2\";"));
        Assert.Equal("v2", await CompileAndRunAsync(compiler, CSharpScript));

        // Unchanged everything — still the current value (no-op incremental build, not a stale one).
        Assert.Equal("v2", await CompileAndRunAsync(compiler, CSharpScript));
    }

    [Fact]
    public async Task Compilation_Should_PickUpAProjectFileChangeAndANewTransitiveReference()
    {
        var inner = WriteProject("Inner",
            "namespace InnerLib; public static class Inner { public static string Value() => \"inner-v1\"; }");
        var lib = WriteProject("Lib",
            GreeterSource("#if USE_INNER\nreturn InnerLib.Inner.Value();\n#else\nreturn \"base\";\n#endif"));
        using var compiler = CreateCompiler(CSharpProjectProfile.Instance, lib);

        Assert.Equal("base", await CompileAndRunAsync(compiler, CSharpScript));

        // Only Lib.csproj changes: a new define plus a new (never built, never restored) transitive
        // project reference — neither the script nor Lib.cs is touched.
        File.WriteAllText(lib, LibraryProjectXml(
            $"""
            <PropertyGroup><DefineConstants>$(DefineConstants);USE_INNER</DefineConstants></PropertyGroup>
            <ItemGroup><ProjectReference Include="{inner}" /></ItemGroup>
            """));
        Assert.Equal("inner-v1", await CompileAndRunAsync(compiler, CSharpScript));

        // A source change two levels down the graph.
        File.WriteAllText(Path.Combine(_root, "Inner", "Inner.cs"),
            "namespace InnerLib; public static class Inner { public static string Value() => \"inner-v2\"; }");
        Assert.Equal("inner-v2", await CompileAndRunAsync(compiler, CSharpScript));
    }

    [Fact]
    public async Task Compilation_Should_FailWithTheLibraryError_AndNotReuseTheOldBinary()
    {
        var libSource = Path.Combine(_root, "Lib", "Lib.cs");
        var lib = WriteProject("Lib", GreeterSource("return \"v1\";"));
        using var compiler = CreateCompiler(CSharpProjectProfile.Instance, lib);

        Assert.Equal("v1", await CompileAndRunAsync(compiler, CSharpScript));

        File.WriteAllText(libSource, GreeterSource("return undefinedLibName;"));

        var broken = await CompileAsync(compiler, CSharpScript);
        Assert.False(broken.Success);
        Assert.Contains(broken.Errors, e => e.Code == "CS0103" && e.File.EndsWith("Lib.cs", StringComparison.Ordinal));
        Assert.False(File.Exists(broken.OutputBinaryFile)); // stale v1 Query.dll must be gone

        // Same broken library, same script — must fail again rather than hit any cached success.
        var brokenAgain = await CompileAsync(compiler, CSharpScript);
        Assert.False(brokenAgain.Success);
        Assert.Contains(brokenAgain.Errors, e => e.Code == "CS0103");
        Assert.False(File.Exists(brokenAgain.OutputBinaryFile));
    }

    // Same shared MsBuildProcessCompiler logic through VbProjectProfile — a VB script consuming a C#
    // library project.
    [Fact]
    public async Task VbCompilation_Should_BuildTheReferencedProject_AndPickUpItsSourceChanges()
    {
        var libSource = Path.Combine(_root, "Lib", "Lib.cs");
        var lib = WriteProject("Lib", GreeterSource("return \"vb-v1\";"));
        using var compiler = CreateCompiler(VbProjectProfile.Instance, lib);

        Assert.Equal("vb-v1", await CompileAndRunAsync(compiler, VbScript));

        File.WriteAllText(libSource, GreeterSource("return \"vb-v2\";"));
        Assert.Equal("vb-v2", await CompileAndRunAsync(compiler, VbScript));
    }
}
