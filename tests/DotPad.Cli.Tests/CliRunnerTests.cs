using DotPad.Cli;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Languages.DotnetCommon;
using Spectre.Console.Testing;
using Xunit;

namespace DotPad.Cli.Tests;

// TODO.md #40: BuildOptions is the one piece of CliRunner.RunAsync that's pure logic (no process
// spawning, no real SDK needed) — mirrors EditorTabItemControl.BuildRunOptions for the GUI's own
// non-debug run path, so these pin down that the two stay equivalent.
public class CliRunnerTests
{
    private static readonly DotnetSdk Platform = new("net10.0", "10.0.100", ".NET 10.0.100", ".NET 10");

    private static RpadFrontMatter CreateFrontMatter(RpadLanguage language = RpadLanguage.CSharp) => new()
    {
        Language = language,
        Kind = RpadKind.Program,
        Packages = new Dictionary<string, Package> { ["Newtonsoft.Json"] = new("13.0.3") },
        Namespaces = ["System.Text"],
        References = ["Some.Native.dll", "Some.Project.csproj", "Some.Vb.vbproj"],
    };

    // One options type for every compiled .NET language (ANALYSIS.md #86/#87) — CSharp/VB used to
    // each get their own byte-for-byte identical options class, so these two tests used to assert
    // different Assert.IsType<> targets for otherwise-identical content; now there's only one type
    // to build regardless of language.
    [Fact]
    public void BuildOptions_Should_ReturnExpectedOptions_ForCSharp()
    {
        var options = CliRunner.BuildOptions(CreateFrontMatter(), Platform, "/tmp/out");

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Equal(Platform, csharpOptions.Platform);
        Assert.Equal("/tmp/out", csharpOptions.OutputDirectory);
        Assert.Equal(RpadKind.Program, csharpOptions.Kind);
        Assert.Equal("13.0.3", csharpOptions.Packages["Newtonsoft.Json"]);
        Assert.Equal(["System.Text"], csharpOptions.Usings);
        Assert.Equal(["Some.Native.dll"], csharpOptions.AssemblyReferences);
        Assert.Equal(["Some.Project.csproj", "Some.Vb.vbproj"], csharpOptions.ProjectReferences);
        Assert.False(string.IsNullOrEmpty(csharpOptions.RuntimeLibPath));
    }

    [Fact]
    public void BuildOptions_Should_ReturnExpectedOptions_ForVb()
    {
        var options = CliRunner.BuildOptions(CreateFrontMatter(RpadLanguage.Vb), Platform, "/tmp/out");

        var vbOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Equal(Platform, vbOptions.Platform);
        Assert.Equal(RpadKind.Program, vbOptions.Kind);
    }

    [Fact]
    public void BuildOptions_Should_DefaultToEmptyCollections_WhenFrontMatterHasNone()
    {
        var frontMatter = new RpadFrontMatter(); // Namespaces/References both null by default

        var options = CliRunner.BuildOptions(frontMatter, Platform, "/tmp/out");

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Empty(csharpOptions.Usings);
        Assert.Empty(csharpOptions.AssemblyReferences);
        Assert.Empty(csharpOptions.ProjectReferences);
    }

    // TODO.md #63: $(CurrentQueryPath) resolves against the currentQueryPath parameter threaded in
    // from RunAsync's own filePath — mirrors EditorTabItemControl.BuildRunOptions's use of
    // _vm.FilePath for the same macro (see ReferencePathMacros.Expand).
    [Fact]
    public void BuildOptions_Should_ExpandCurrentQueryPathMacro_InReferences()
    {
        // Path.GetDirectoryName normalizes to the platform's own separator (backslashes on
        // Windows) — computing the expected directory the same way, rather than hardcoding a
        // forward-slash-only "/home/user/..." literal, keeps this correct on every CI OS.
        var currentQueryPath = Path.Combine("home", "user", "scripts", "query.rpad");
        var expectedDir = Path.GetDirectoryName(currentQueryPath);
        var frontMatter = new RpadFrontMatter
        {
            References = ["$(CurrentQueryPath)/Lib.dll", "$(CurrentQueryPath)/Shared.csproj"],
        };

        var options = CliRunner.BuildOptions(frontMatter, Platform, "/tmp/out", currentQueryPath);

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Equal([expectedDir + "/Lib.dll"], csharpOptions.AssemblyReferences);
        Assert.Equal([expectedDir + "/Shared.csproj"], csharpOptions.ProjectReferences);
    }

    [Fact]
    public void BuildOptions_Should_LeaveCurrentQueryPathMacroUnexpanded_WhenNoFilePathGiven()
    {
        var frontMatter = new RpadFrontMatter { References = ["$(CurrentQueryPath)/Lib.dll"] };

        var options = CliRunner.BuildOptions(frontMatter, Platform, "/tmp/out");

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Equal(["$(CurrentQueryPath)/Lib.dll"], csharpOptions.AssemblyReferences);
    }

    [Fact]
    public void BuildOptions_Should_ExpandRuntimeDirectoryAndMyDocumentsMacros_InReferences()
    {
        var frontMatter = new RpadFrontMatter { References = ["$(RuntimeDirectory)/System.dll"] };

        var options = CliRunner.BuildOptions(frontMatter, Platform, "/tmp/out");

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        var expanded = Assert.Single(csharpOptions.AssemblyReferences);
        Assert.DoesNotContain("$(RuntimeDirectory)", expanded);
        Assert.EndsWith("/System.dll", expanded);
    }

    [Fact]
    public async Task RunAsync_Should_ReturnError_WhenNoArgsGiven()
    {
        var stdout = new TestConsole();
        var stderr = new TestConsole();

        var exitCode = await CliRunner.RunAsync([], stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage:", stderr.Output);
    }

    [Fact]
    public async Task RunAsync_Should_ReturnError_WhenFileDoesNotExist()
    {
        var stdout = new TestConsole();
        var stderr = new TestConsole();

        var exitCode = await CliRunner.RunAsync(
            ["/no/such/file.rpad"], stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("File not found", stderr.Output);
    }

    [Fact]
    public async Task RunAsync_Should_ReturnError_WhenFileIsNotAValidRpadDocument()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "not a .rpad document at all");
            var stdout = new TestConsole();
            var stderr = new TestConsole();

            var exitCode = await CliRunner.RunAsync(
                [path], stdout, stderr, CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains("Failed to parse", stderr.Output);
        }
        finally { File.Delete(path); }
    }

    // ── convert (TODO.md #63) ────────────────────────────────────────────────────

    private const string ValidLinq = """
        <Query Kind="Statements">
          <NuGetReference>Newtonsoft.Json</NuGetReference>
        </Query>

        Console.WriteLine("hi");
        """;

    [Fact]
    public async Task ConvertLinqAsync_Should_ReturnError_WhenNoInputPathGiven()
    {
        var stdout = new TestConsole();
        var stderr = new TestConsole();

        var exitCode = await CliRunner.RunAsync(["convert"], stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage:", stderr.Output);
    }

    [Fact]
    public async Task ConvertLinqAsync_Should_ReturnError_WhenTheInputFileDoesNotExist()
    {
        var stdout = new TestConsole();
        var stderr = new TestConsole();

        var exitCode = await CliRunner.RunAsync(
            ["convert", "/no/such/file.linq"], stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("File not found", stderr.Output);
    }

    [Fact]
    public async Task ConvertLinqAsync_Should_WriteASiblingRpad_WhenNoOutputPathGiven()
    {
        var dir = Directory.CreateTempSubdirectory("dotpad-cli-convert-test").FullName;
        try
        {
            var linqPath = Path.Combine(dir, "MyQuery.linq");
            await File.WriteAllTextAsync(linqPath, ValidLinq, CancellationToken.None);
            // Wide enough that Spectre's line-wrapping never splits the "Wrote <path>" message —
            // TestConsole defaults to an 80-column width, and the machine's temp directory (which
            // varies by username/profile) can easily push that single unbroken path past 80 chars,
            // making the plain Assert.Contains below fail non-deterministically depending on machine.
            var stdout = new TestConsole();
            stdout.Profile.Width = 4096;
            var stderr = new TestConsole();

            var exitCode = await CliRunner.RunAsync(["convert", linqPath], stdout, stderr, CancellationToken.None);

            Assert.Equal(0, exitCode);
            var expectedOutputPath = Path.Combine(dir, "MyQuery.rpad");
            Assert.True(File.Exists(expectedOutputPath));
            Assert.Contains(expectedOutputPath, stdout.Output);

            var parsed = RpadParser.TryParse(await File.ReadAllTextAsync(expectedOutputPath, CancellationToken.None));
            Assert.NotNull(parsed);
            Assert.Equal(RpadKind.Statements, parsed!.FrontMatter.Kind);
            Assert.Contains("Newtonsoft.Json", parsed.FrontMatter.Packages.Keys);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ConvertLinqAsync_Should_WriteToTheGivenOutputPath_AndOverwriteIt()
    {
        var dir = Directory.CreateTempSubdirectory("dotpad-cli-convert-test").FullName;
        try
        {
            var linqPath = Path.Combine(dir, "MyQuery.linq");
            await File.WriteAllTextAsync(linqPath, ValidLinq, CancellationToken.None);
            var outputPath = Path.Combine(dir, "Somewhere.rpad");
            await File.WriteAllTextAsync(outputPath, "will be overwritten", CancellationToken.None);
            var stdout = new TestConsole();
            var stderr = new TestConsole();

            var exitCode = await CliRunner.RunAsync(
                ["convert", linqPath, outputPath], stdout, stderr, CancellationToken.None);

            Assert.Equal(0, exitCode);
            var written = await File.ReadAllTextAsync(outputPath, CancellationToken.None);
            Assert.DoesNotContain("will be overwritten", written);
            Assert.NotNull(RpadParser.TryParse(written));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ConvertLinqAsync_Should_ReturnError_ForAnUnsupportedQueryKind()
    {
        var dir = Directory.CreateTempSubdirectory("dotpad-cli-convert-test").FullName;
        try
        {
            var linqPath = Path.Combine(dir, "MyQuery.linq");
            await File.WriteAllTextAsync(linqPath, "<Query Kind=\"SQL\" />\n\nSELECT 1\n", CancellationToken.None);
            var stdout = new TestConsole();
            var stderr = new TestConsole();

            var exitCode = await CliRunner.RunAsync(["convert", linqPath], stdout, stderr, CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains("SQL", stderr.Output);
            Assert.False(File.Exists(Path.Combine(dir, "MyQuery.rpad")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
