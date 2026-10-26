using RunPad.Contracts.Models;
using RunPad.Contracts.Connections;
using RunPad.Format.RPad;
using RunPad.Services;
using Xunit;

namespace RunPad.NonUI.Tests;

public sealed class ExecutionRequestBuilderTests
{
    [Fact]
    public void Build_PreservesMetadataAndRuntimeOptions()
    {
        var frontMatter = new RpadFrontMatter
        {
            Kind = RpadKind.Program,
            Namespaces = ["System.Text"],
            Arguments = [new() { Name = "env", Default = RpadValue.Of("dev") }],
        };
        var request = ExecutionRequestBuilder.Build(frontMatter, "query.rpad", null, "scripts",
            new DotnetSdk("10.0.100", "net10.0", "10.0.0", ".NET 10"), "out", "work",
            "dotnet", "debugger", true, false);

        Assert.Equal(RpadKind.Program, request.Kind);
        Assert.Equal(["System.Text"], request.Usings);
        Assert.Equal("dev", request.Arguments[1]);
        Assert.Equal("query.rpad", request.CurrentQueryPath);
        Assert.Equal("scripts", request.ReferenceBasePath);
        Assert.True(request.BreakOnAllExceptions);
        Assert.False(request.BreakOnUncaughtExceptions);
    }

    [Fact]
    public void Build_SupportsUntitledDefaultsAndGeneratedConnections()
    {
        var connections = new GeneratedConnections("", [], []);
        var request = ExecutionRequestBuilder.Build(null, null, connections, null, null, null, "work", null, null, false, true);

        Assert.Equal(RpadKind.Statements, request.Kind);
        Assert.Same(connections, request.Connections);
        Assert.Null(request.CurrentQueryPath);
        Assert.Equal("work", request.WorkingDirectory);
    }
}
