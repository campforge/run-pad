using RunPad.Services.Connections;
using Xunit;

namespace RunPad.NonUI.Tests;

public sealed class ConnectionGroupingTests
{
    [Fact]
    public void Ungrouped_IsStable_AndDeleteMovesChildrenWithoutDeletingConnections()
    {
        var model = new ConnectionGroupingModel();
        var parent = model.Create("Databases");
        var child = model.Create("Local", parent.Id);
        model.MoveConnection("db", child.Id);
        model.Delete(parent.Id);
        Assert.Equal(child.Id, model.GroupFor("db"));
        Assert.Contains(model.Groups, g => g.Id == child.Id && g.ParentId is null);
    }

    [Fact]
    public void InvalidAssignments_MigrateToUngrouped()
    {
        var model = new ConnectionGroupingModel(new ConnectionGroupingSnapshot([], new Dictionary<string, string> { ["x"] = "missing" }, null));
        Assert.Equal(ConnectionGroupingModel.UngroupedId, model.GroupFor("x"));
    }

    [Fact]
    public void Nesting_IsBounded()
    {
        var model = new ConnectionGroupingModel();
        var a = model.Create("a");
        var b = model.Create("b", a.Id);
        var c = model.Create("c", b.Id);
        Assert.Throws<InvalidOperationException>(() => model.Create("d", c.Id));
    }

    [Fact]
    public void Sidecar_ShouldRoundTripGroupingWithoutTouchingConnectionFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "runpad-grouping-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var connections = Path.Combine(directory, "connections.json");
            File.WriteAllText(connections, "original");
            var store = new FileConnectionGroupingStore(connections);
            var model = new ConnectionGroupingModel();
            var group = model.Create("Databases");
            model.MoveConnection("db", group.Id);
            store.Save(model.Snapshot("v1"));
            var loaded = new ConnectionGroupingModel(store.Load());
            Assert.Equal(group.Id, loaded.GroupFor("db"));
            Assert.Equal("original", File.ReadAllText(connections));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
