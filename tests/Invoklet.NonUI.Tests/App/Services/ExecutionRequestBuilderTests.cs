using Invoklet.Contracts.Connections;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Services;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class ExecutionRequestBuilderTests
{
    [Fact]
    public void Build_PreservesMetadataAndRuntimeOptions()
    {
        var frontMatter = new MdRunFrontMatter
        {
            Kind = MdRunKind.Program,
            Namespaces = ["System.Text"],
            Arguments = [new() { Name = "env", Default = MdRunValue.Of("dev") }],
        };
        var request = ExecutionRequestBuilder.Build(frontMatter, "query.inv", null, "scripts",
            new DotnetSdk("10.0.100", "net10.0", "10.0.0", ".NET 10"), "out", "work",
            "dotnet", "debugger", true, false);

        Assert.Equal(MdRunKind.Program, request.Kind);
        Assert.Equal(["System.Text"], request.Usings);
        Assert.Equal("dev", request.Arguments[1]);
        Assert.Equal("query.inv", request.CurrentQueryPath);
        Assert.Equal("scripts", request.ReferenceBasePath);
        Assert.True(request.BreakOnAllExceptions);
        Assert.False(request.BreakOnUncaughtExceptions);
    }

    [Fact]
    public void Build_SupportsUntitledDefaultsAndGeneratedConnections()
    {
        var connections = new GeneratedConnections("", [], []);
        var request = ExecutionRequestBuilder.Build(null, null, connections, null, null, null, "work", null, null, false, true);

        Assert.Equal(MdRunKind.Statements, request.Kind);
        Assert.Same(connections, request.Connections);
        Assert.Null(request.CurrentQueryPath);
        Assert.Equal("work", request.WorkingDirectory);
    }
}
