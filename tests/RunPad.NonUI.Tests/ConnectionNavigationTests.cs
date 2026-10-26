using Moq;
using RunPad.Connectors.Http;
using RunPad.Contracts.Connections;
using RunPad.Format.RPad;
using RunPad.Services.Connections;
using RunPad.Services.Secrets;
using RunPad.UserControls.Models;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.NonUI.Tests;

public sealed class ConnectionGroupingModelTests
{
    [Fact]
    public void MoveGroup_Should_RejectCyclesAndNestingBeyondTheLimit()
    {
        var model = new ConnectionGroupingModel();
        var a = model.Create("a");
        var b = model.Create("b", a.Id);
        var c = model.Create("c", b.Id);
        var d = model.Create("d");
        var e = model.Create("e", d.Id);

        Assert.Throws<InvalidOperationException>(() => model.MoveGroup(a.Id, c.Id));
        Assert.Throws<InvalidOperationException>(() => model.MoveGroup(a.Id, a.Id));
        Assert.False(model.CanMoveGroup(d.Id, b.Id)); // d with its child would reach depth 4
        Assert.True(model.CanMoveGroup(e.Id, b.Id));

        model.MoveGroup(e.Id, b.Id);
        Assert.Equal(b.Id, model.Find(e.Id)!.ParentId);
        Assert.Equal(3, model.DepthOf(e.Id));
        model.MoveGroup(c.Id, null);
        Assert.Null(model.Find(c.Id)!.ParentId);
    }

    [Fact]
    public void Reorder_Should_SwapNeighboursAndStopAtTheEnds()
    {
        var model = new ConnectionGroupingModel();
        var first = model.Create("First");
        var second = model.Create("Second");
        var third = model.Create("Third");

        Assert.True(model.Reorder(third.Id, -1));
        Assert.Equal(["First", "Third", "Second"], model.ChildrenOf(null).Select(g => g.Name));
        Assert.False(model.Reorder(first.Id, -1));
        Assert.False(model.Reorder(second.Id, 1));
    }

    [Fact]
    public void Delete_Should_MoveSubgroupsToTheParentAfterItsOwnGroups()
    {
        var model = new ConnectionGroupingModel();
        var root = model.Create("Root");
        var keep = model.Create("Keep", root.Id);
        var removed = model.Create("Removed", root.Id);
        var inner = model.Create("Inner", removed.Id);
        model.MoveConnection("api", removed.Id);

        model.Delete(removed.Id);

        Assert.Equal(["Keep", "Inner"], model.ChildrenOf(root.Id).Select(g => g.Name));
        Assert.Equal(root.Id, model.GroupFor("api"));
        Assert.Equal(root.Id, model.Find(inner.Id)!.ParentId);
        Assert.Equal(keep.Id, model.ChildrenOf(root.Id)[0].Id);
    }

    [Fact]
    public void RenameAndRetain_Should_KeepAssignmentsInStepWithTheList()
    {
        var model = new ConnectionGroupingModel();
        var group = model.Create("Group");
        model.MoveConnection("old", group.Id);
        model.MoveConnection("gone", group.Id);

        model.RenameConnection("old", "new");
        model.Retain(["new"]);

        Assert.Equal(group.Id, model.GroupFor("new"));
        Assert.Equal(ConnectionGroupingModel.UngroupedId, model.GroupFor("old"));
        Assert.False(model.Assignments.ContainsKey("gone"));
    }

    [Fact]
    public void ADamagedSnapshot_Should_KeepItsGroupsAtTheTopLevel()
    {
        var snapshot = new ConnectionGroupingSnapshot(
        [
            new ConnectionGroup("a", "A", "b", 0),
            new ConnectionGroup("b", "B", "a", 0), // a cycle
            new ConnectionGroup("c", "C", "missing", 0),
            new ConnectionGroup("a", "Duplicate", null, 1),
            new ConnectionGroup(ConnectionGroupingModel.UngroupedId, "Fake", null, 2),
        ], new Dictionary<string, string> { ["db"] = "c" }, null);

        var model = new ConnectionGroupingModel(snapshot);

        Assert.Equal(["a", "b", "c"], model.Groups.Select(g => g.Id));
        Assert.Null(model.Find("c")!.ParentId);
        Assert.True(model.Find("a")!.ParentId is null || model.Find("b")!.ParentId is null);
        Assert.Equal("c", model.GroupFor("db"));
    }

    [Fact]
    public void TheStore_Should_ReportADamagedOrNewerFileAndLeaveItAsItIs()
    {
        var folder = Directory.CreateTempSubdirectory("runpad-groups").FullName;
        try
        {
            var store = new FileConnectionGroupingStore(Path.Combine(folder, "connections.json"));
            Assert.Empty(store.Load().Groups);

            File.WriteAllText(store.FilePath, "{ not json");
            Assert.Throws<InvalidDataException>(() => store.Load());
            Assert.Equal("{ not json", File.ReadAllText(store.FilePath));

            File.WriteAllText(store.FilePath, """{ "Format": 99, "Groups": [] }""");
            Assert.Throws<InvalidDataException>(() => store.Load());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public sealed class ConnectionNavigationViewModelTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("runpad-connection-tree").FullName;
    private string ConnectionsPath => Path.Combine(_folder, "connections.json");
    private string GroupsPath => ConnectionsPath + ".groups.json";

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static RpadConnection Connection(string name, string driver = "http") => new() { Name = name, Driver = driver };

    private async Task<ConnectionNavigationViewModel> CreateAsync(params RpadConnection[] connections)
    {
        var store = new FileConnectionStore(ConnectionsPath);
        store.Save(connections, null);
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new HttpConnectionDriver());
        var vm = new ConnectionNavigationViewModel(store, drivers);
        await vm.RefreshAsync();
        return vm;
    }

    [Fact]
    public async Task TheTree_Should_ShowGroupsInOrderAndUngroupedLast()
    {
        var model = new ConnectionGroupingModel();
        var apis = model.Create("APIs");
        var local = model.Create("Local", apis.Id);
        model.MoveConnection("billing", local.Id);
        new FileConnectionGroupingStore(ConnectionsPath).Save(model.Snapshot());

        var vm = await CreateAsync(Connection("billing"), Connection("warehouse", "snowflake"));

        Assert.Equal(["APIs", "Ungrouped"], vm.Nodes.Select(n => n.Name));
        var billing = Assert.Single(Assert.Single(vm.Nodes[0].Children!).Children!);
        Assert.Equal("billing", billing.Name);
        Assert.Equal("Icon.DriverHttp", billing.IconKey);
        var warehouse = Assert.Single(vm.Nodes[1].Children!);
        Assert.True(warehouse.IsDriverMissing);
        Assert.Equal("Icon.DriverDatabase", warehouse.IconKey);
        Assert.Contains("driver not available", warehouse.AccessibleName);
        Assert.Equal("APIs group, 1 connection", vm.Nodes[0].AccessibleName);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task AnEmptyList_Should_ShowTheEmptyState()
    {
        var vm = await CreateAsync();
        Assert.True(vm.IsEmpty);
        Assert.Equal(["Ungrouped"], vm.Nodes.Select(n => n.Name));
    }

    [Fact]
    public async Task AttachedBadges_Should_FollowTheScriptsOwnConnections()
    {
        var vm = await CreateAsync(Connection("billing"), Connection("orders"));
        vm.SetAttached([Connection("orders")]);

        var rows = vm.Nodes.Single().Children!;
        Assert.False(rows[0].IsAttached);
        Assert.True(rows[1].IsAttached);
        Assert.Contains("used by this script", rows[1].AccessibleName);

        vm.SetAttached(null);
        Assert.DoesNotContain(rows, r => r.IsAttached);
    }

    [Fact]
    public async Task OrganizationChanges_Should_PersistWithoutTouchingTheConnectionList()
    {
        var vm = await CreateAsync(Connection("billing"), Connection("orders"));
        var list = File.ReadAllBytes(ConnectionsPath);

        var group = vm.CreateGroup(null)!;
        Assert.Equal("New Group", group.Name);
        Assert.True(vm.RenameGroup(group, "  Payments "));
        Assert.True(vm.MoveConnection(vm.Find("billing", isConnection: true)!, group.Id));
        var sub = vm.CreateGroup(vm.Find(group.Id, isConnection: false))!;
        Assert.True(vm.MoveConnection(vm.Find("orders", isConnection: true)!, sub.Id));

        Assert.Equal(list, File.ReadAllBytes(ConnectionsPath));
        var reloaded = new ConnectionNavigationViewModel(new FileConnectionStore(ConnectionsPath));
        await reloaded.RefreshAsync();
        var payments = reloaded.Nodes[0];
        Assert.Equal("Payments", payments.Name);
        Assert.Equal(["New Group", "billing"], payments.Children!.Select(c => c.Name));
        Assert.Equal("orders", Assert.Single(payments.Children![0].Children!).Name);

        Assert.True(reloaded.DeleteGroup(payments));
        Assert.Equal(["New Group", "Ungrouped"], reloaded.Nodes.Select(n => n.Name));
        Assert.Equal(["billing"], reloaded.Nodes[1].Children!.Select(c => c.Name));
        Assert.Equal(list, File.ReadAllBytes(ConnectionsPath));
    }

    [Fact]
    public async Task NestingBeyondTheLimit_Should_BeRejectedWithAVisibleNotice()
    {
        var vm = await CreateAsync(Connection("billing"));
        var a = vm.CreateGroup(null)!;
        var b = vm.CreateGroup(a)!;
        var c = vm.CreateGroup(b)!;

        Assert.Null(vm.CreateGroup(c));
        Assert.Equal(ConnectionGroupingModel.NestingLimitMessage, vm.Notice);
        vm.DismissNotice();
        Assert.False(vm.HasNotice);
    }

    [Fact]
    public async Task MoveTargets_Should_LeaveOutTheGroupItsSubgroupsAndTooDeepPlaces()
    {
        var vm = await CreateAsync(Connection("billing"));
        var a = vm.CreateGroup(null, "A")!;
        var b = vm.CreateGroup(a, "B")!;
        var other = vm.CreateGroup(null, "Other")!;
        vm.RenameGroup(other, "Other");

        var forA = vm.MoveTargets(vm.Find(a.Id, isConnection: false)!);
        Assert.Equal(["Top Level", "Other"], forA.Select(t => t.Path));

        var forConnection = vm.MoveTargets(vm.Find("billing", isConnection: true)!);
        Assert.Equal(["Ungrouped", "A", "A / B", "Other"], forConnection.Select(t => t.Path));
        Assert.Empty(vm.MoveTargets(vm.Nodes.Single(n => n.IsUngrouped)));
        Assert.NotNull(b);
    }

    [Fact]
    public async Task Expansion_Should_SurviveRebuildsAndRefreshes()
    {
        var vm = await CreateAsync(Connection("billing"));
        var group = vm.CreateGroup(null, "Group")!;
        group.IsExpanded = false;
        vm.CreateGroup(null, "Another");
        Assert.False(vm.Find(group.Id, isConnection: false)!.IsExpanded);
        await vm.RefreshAsync();
        Assert.False(vm.Find(group.Id, isConnection: false)!.IsExpanded);
        Assert.True(vm.Nodes.Single(n => n.IsUngrouped).IsExpanded);
    }

    [Fact]
    public async Task ADamagedGroupsFile_Should_ShowTheConnectionsAndNotBeOverwritten()
    {
        File.WriteAllText(GroupsPath, "{ damaged");
        var vm = await CreateAsync(Connection("billing"));

        Assert.True(vm.HasError);
        Assert.False(vm.CanEditGroups);
        Assert.Equal("billing", Assert.Single(vm.Nodes.Single().Children!).Name);
        Assert.Null(vm.CreateGroup(null));
        Assert.Empty(vm.MoveTargets(vm.Find("billing", isConnection: true)!));
        Assert.Equal("{ damaged", File.ReadAllText(GroupsPath));
    }

    [Fact]
    public async Task AnUnreadableConnectionList_Should_ShowAnError()
    {
        File.WriteAllText(ConnectionsPath, "{ damaged");
        var vm = new ConnectionNavigationViewModel(new FileConnectionStore(ConnectionsPath));
        await vm.RefreshAsync();
        Assert.True(vm.HasError);
        Assert.False(vm.IsEmpty);
        Assert.Equal("{ damaged", File.ReadAllText(ConnectionsPath));
    }

    [Fact]
    public async Task RenamingInTheConnectionsWindow_Should_KeepTheGroup_AndRemovingShouldForgetIt()
    {
        var tree = await CreateAsync(Connection("billing"), Connection("orders"));
        var group = tree.CreateGroup(null, "APIs")!;
        tree.MoveConnection(tree.Find("billing", isConnection: true)!, group.Id);
        tree.MoveConnection(tree.Find("orders", isConnection: true)!, group.Id);

        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new HttpConnectionDriver());
        var secrets = Mock.Of<ISecretStore>(s => s.LoadSnapshot() == new SecretStoreSnapshot(Array.Empty<Secret>(), null));
        var window = new ConnectionsWindowViewModel(new FileConnectionStore(ConnectionsPath), drivers, secrets);
        window.Connections[0].Name = "invoices";
        window.SelectedConnection = window.Connections[1];
        window.RemoveCommand.Execute().Subscribe();
        Assert.True(window.Save());

        await tree.RefreshAsync();
        var apis = tree.Nodes[0];
        Assert.Equal("invoices", Assert.Single(apis.Children!).Name);
        var stored = new FileConnectionGroupingStore(ConnectionsPath).Load();
        Assert.Equal(["invoices"], stored.Assignments.Keys);
    }
}
