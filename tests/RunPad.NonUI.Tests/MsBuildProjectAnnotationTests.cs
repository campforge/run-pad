using RunPad.Languages.DotnetCommon.Compiler;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// MSBuild appends " [<project path>]" to every diagnostic. On macOS that path comes back
// symlink-resolved ("/private/var/...") while the compiler's own project path is "/var/..." — the old
// exact Replace missed it and the annotation leaked into every Results message (macOS CI). Pure
// string logic: no real build, so these run in the fast CI lanes too.
public class MsBuildProjectAnnotationTests
{
    private const string MacProject = "/var/folders/6z/abc/T/runpad-compiler-testL7OZyc/Query.csproj";
    private const string WindowsProject = @"C:\Users\runneradmin\AppData\Local\Temp\runpad-compiler-test\Query.csproj";

    [Theory]
    // Exact path (Linux, and Windows-shaped output).
    [InlineData("The name 'x' does not exist. [" + MacProject + "]", MacProject)]
    [InlineData("The name 'x' does not exist. [" + WindowsProject + "]", WindowsProject)]
    // macOS: real `dotnet build` output reports the /var temp directory as /private/var.
    [InlineData("The name 'x' does not exist. [/private" + MacProject + "]", MacProject)]
    public void Should_RemoveTerminalProjectAnnotation(string message, string projectPath)
    {
        Assert.Equal("The name 'x' does not exist.", MsBuildProcessCompiler.RemoveProjectAnnotation(message, projectPath));
    }

    [Fact]
    public void Should_KeepOtherBracketedContent_WhenRemovingAnnotation()
    {
        var message = "Package 'Foo [1.0, 2.0)' not found [see docs] [/private" + MacProject + "]";

        Assert.Equal(
            "Package 'Foo [1.0, 2.0)' not found [see docs]",
            MsBuildProcessCompiler.RemoveProjectAnnotation(message, MacProject));
    }

    [Theory]
    // Not a project annotation at all.
    [InlineData("Type 'int[]' is invalid [CS-extra]")]
    // A different project's annotation.
    [InlineData("Error in referenced project [/var/folders/6z/abc/T/other/Lib.csproj]")]
    // Our project path, but not as the terminal annotation.
    [InlineData("Could not load [" + MacProject + "] for evaluation")]
    // Our project path inside a bracket that isn't just a path prefix.
    [InlineData("Restore failed [see " + MacProject + "]")]
    public void Should_LeaveMessageUnchanged_WhenThereIsNoTerminalAnnotationForThisProject(string message)
    {
        Assert.Equal(message, MsBuildProcessCompiler.RemoveProjectAnnotation(message, MacProject));
    }
}
