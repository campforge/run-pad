using DotPad.Cli;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Languages.VB;
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

    [Fact]
    public void BuildOptions_Should_ReturnCSharpOptions_ForCSharp()
    {
        var options = CliRunner.BuildOptions(CreateFrontMatter(), "csharp", Platform, "/tmp/out");

        var csharpOptions = Assert.IsType<CSharpLanguageHostOptions>(options);
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
    public void BuildOptions_Should_ReturnVbOptions_ForVb()
    {
        var options = CliRunner.BuildOptions(CreateFrontMatter(RpadLanguage.Vb), "vb", Platform, "/tmp/out");

        var vbOptions = Assert.IsType<VbLanguageHostOptions>(options);
        Assert.Equal(Platform, vbOptions.Platform);
        Assert.Equal(RpadKind.Program, vbOptions.Kind);
    }

    [Fact]
    public void BuildOptions_Should_DefaultToEmptyCollections_WhenFrontMatterHasNone()
    {
        var frontMatter = new RpadFrontMatter(); // Namespaces/References both null by default

        var options = CliRunner.BuildOptions(frontMatter, "csharp", Platform, "/tmp/out");

        var csharpOptions = Assert.IsType<CSharpLanguageHostOptions>(options);
        Assert.Empty(csharpOptions.Usings);
        Assert.Empty(csharpOptions.AssemblyReferences);
        Assert.Empty(csharpOptions.ProjectReferences);
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
}
