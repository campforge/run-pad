using Invoklet.Connectors.Http;
using Invoklet.Contracts.Connections;
using Invoklet.Format.MdRun;
using Invoklet.Services.Connections;
using Invoklet.Services.Examples;
using Invoklet.Services.Secrets;
using Invoklet.UserControls.Models;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.UserControls.Models;

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
        var folder = Directory.CreateTempSubdirectory("Invoklet-groups").FullName;
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
    private readonly string _folder = Directory.CreateTempSubdirectory("Invoklet-connection-tree").FullName;
    private string ConnectionsPath => Path.Combine(_folder, "connections.json");
    private string GroupsPath => ConnectionsPath + ".groups.json";

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static MdRunConnection Connection(string name, string driver = "http") => new()
    {
        Name = name,
        Driver = driver,
        Params = driver == "http" ? new(StringComparer.Ordinal) { ["baseAddress"] = MdRunValue.Of($"https://{name}.test/") } : new(StringComparer.Ordinal),
    };

    private sealed class PackagedDriver : IConnectionDriver
    {
        public string Id => "packaged";
        public string DisplayName => "Packaged";
        public ConnectionCategory Category => ConnectionCategory.Database;
        public IReadOnlyList<ConnectionParam> Params { get; } = [];
        public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string> { ["Example.Client"] = "1.2.3" };
        public IReadOnlyList<string> Validate(MdRunConnection connection) => [];
        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(true, ""));
        public ConnectionClientCode CreateClient(MdRunConnection connection, ConnectionCodeContext context) => new("");
    }

    private async Task<ConnectionNavigationViewModel> CreateAsync(params MdRunConnection[] connections)
    {
        var store = new FileConnectionStore(ConnectionsPath);
        store.Save(connections, null);
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new HttpConnectionDriver());
        drivers.Register(new PackagedDriver());
        var vm = new ConnectionNavigationViewModel(store, drivers);
        await vm.RefreshAsync();
        return vm;
    }

    [Fact]
    public async Task ConnectionsWithProblems_Should_BeMarked_AlsoOnTheirFoldedGroup()
    {
        var model = new ConnectionGroupingModel();
        var apis = model.Create("APIs");
        model.MoveConnection("broken", apis.Id);
        new FileConnectionGroupingStore(ConnectionsPath).Save(model.Snapshot());
        var broken = new MdRunConnection { Name = "broken", Driver = "http" }; // no base address

        var vm = await CreateAsync(broken, Connection("fine"), Connection("warehouse", "snowflake"));

        var group = vm.Nodes[0];
        var brokenRow = Assert.Single(group.Children!);
        Assert.Equal("The base address is missing.", brokenRow.Problem);
        Assert.Contains("problem: The base address is missing.", brokenRow.AccessibleName);
        Assert.Contains("The base address is missing.", brokenRow.ToolTip);
        Assert.True(group.HasProblemInside);
        Assert.True(group.ShowsProblem);
        Assert.Contains("has connections with problems", group.AccessibleName);

        Assert.False(vm.Find("fine", isConnection: true)!.HasProblem);
        Assert.Equal("This Invoklet has no 'snowflake' driver.", vm.Find("warehouse", isConnection: true)!.Problem);
    }

    [Fact]
    public async Task AddingToTheScript_Should_WriteTheConnection_AndAskOnlyAboutPackages()
    {
        var vm = await CreateAsync(Connection("billing"), Connection("orders"), new MdRunConnection { Name = "warehouse", Driver = "packaged" });
        var frontMatter = new MdRunFrontMatter { Connections = [Connection("orders")] };
        var asked = new List<string>();
        var answer = false;
        Task<bool> Confirm(string message) { asked.Add(message); return Task.FromResult(answer); }

        Assert.True(await vm.AddToScriptAsync(frontMatter, null, "billing", Confirm));
        Assert.Equal(["orders", "billing"], frontMatter.Connections.Select(c => c.Name));
        Assert.Empty(asked); // HTTP brings no packages
        Assert.True(vm.Find("billing", isConnection: true)!.IsAttached);

        Assert.False(await vm.AddToScriptAsync(frontMatter, null, "warehouse", Confirm));
        Assert.Contains("Example.Client 1.2.3", Assert.Single(asked));
        Assert.Equal(2, frontMatter.Connections.Count); // declined: nothing changes
        Assert.Empty(frontMatter.Packages);

        answer = true;
        Assert.True(await vm.AddToScriptAsync(frontMatter, null, "warehouse", Confirm));
        Assert.Equal(["orders", "billing", "warehouse"], frontMatter.Connections.Select(c => c.Name));
        Assert.Equal("1.2.3", frontMatter.Packages["Example.Client"].Version);

        Assert.False(await vm.AddToScriptAsync(frontMatter, null, "orders", Confirm));
        Assert.Equal("'orders' is already in this script.", vm.Notice);
        Assert.False(await vm.AddToScriptAsync(frontMatter, null, "gone", Confirm));
        Assert.Equal("'gone' is no longer in your connections.", vm.Notice);
        Assert.Equal(3, frontMatter.Connections.Count);
    }

    [Fact]
    public async Task RemovingFromTheScript_Should_DropTheConnection_AndItsBadge_ButKeepPackages()
    {
        var vm = await CreateAsync(Connection("billing"), Connection("orders"));
        var frontMatter = new MdRunFrontMatter
        {
            Connections = [Connection("billing"), Connection("orders")],
            Packages = new() { ["Example.Client"] = new Package("1.2.3") },
        };
        vm.SetAttached(frontMatter.Connections);
        Assert.True(vm.Find("orders", isConnection: true)!.IsAttached);

        Assert.True(vm.RemoveFromScript(frontMatter, "orders"));
        Assert.Equal(["billing"], frontMatter.Connections.Select(c => c.Name));
        Assert.False(vm.Find("orders", isConnection: true)!.IsAttached);
        Assert.True(vm.Find("billing", isConnection: true)!.IsAttached);
        Assert.True(frontMatter.Packages.ContainsKey("Example.Client"));

        Assert.False(vm.RemoveFromScript(frontMatter, "orders")); // not in the script any more
    }

    [Fact]
    public async Task AddingToTheScript_Should_NotCommit_WhenDisposedWhilePackageConfirmationIsOpen()
    {
        var vm = await CreateAsync(new MdRunConnection { Name = "warehouse", Driver = "packaged" });
        var frontMatter = new MdRunFrontMatter { Connections = [Connection("existing")] };
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var add = vm.AddToScriptAsync(frontMatter, null, "warehouse", async _ =>
        {
            entered.SetResult(true);
            return await decision.Task;
        });

        await entered.Task;
        vm.Dispose();
        decision.SetResult(true);

        Assert.False(await add);
        Assert.Equal(["existing"], frontMatter.Connections.Select(c => c.Name));
        Assert.Empty(frontMatter.Packages ?? []);
    }

    [Fact]
    public async Task TheTree_Should_ShowGroupsFirst_ThenConnectionsOutsideAnyGroupAtTheTopLevel()
    {
        var model = new ConnectionGroupingModel();
        var apis = model.Create("APIs");
        var local = model.Create("Local", apis.Id);
        model.MoveConnection("billing", local.Id);
        new FileConnectionGroupingStore(ConnectionsPath).Save(model.Snapshot());

        var vm = await CreateAsync(Connection("billing"), Connection("warehouse", "snowflake"));

        Assert.Equal(["APIs", "warehouse"], vm.Nodes.Select(n => n.Name));
        var billing = Assert.Single(Assert.Single(vm.Nodes[0].Children!).Children!);
        Assert.Equal("billing", billing.Name);
        Assert.Equal("Icon.DriverHttp", billing.IconKey);
        var warehouse = vm.Nodes[1];
        Assert.True(warehouse.IsConnection);
        Assert.Null(warehouse.Children);
        Assert.True(warehouse.IsDriverMissing);
        vm.SetAttached([]);
        Assert.False(vm.CanAddToScript(warehouse)); // no driver to build its client with
        Assert.Equal("Icon.DriverDatabase", warehouse.IconKey);
        Assert.Contains("problem: This Invoklet has no 'snowflake' driver.", warehouse.AccessibleName);
        Assert.Equal("APIs group, 1 connection", vm.Nodes[0].AccessibleName);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task AnEmptyList_Should_ShowTheEmptyState()
    {
        var vm = await CreateAsync();
        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Nodes);

        vm.CreateGroup(null, "Later");
        Assert.False(vm.IsEmpty); // a group alone is something to show
    }

    [Fact]
    public async Task AttachedBadges_Should_FollowTheScriptsOwnConnections()
    {
        var vm = await CreateAsync(Connection("billing"), Connection("orders"));
        vm.SetAttached([Connection("orders")]);

        var rows = vm.Nodes;
        Assert.False(rows[0].IsAttached);
        Assert.True(rows[1].IsAttached);
        Assert.Contains("used by this script", rows[1].AccessibleName);

        Assert.True(vm.HasActiveScript);
        Assert.True(vm.CanAddToScript(rows[0]));
        Assert.False(vm.CanAddToScript(rows[1])); // already in the script

        vm.SetAttached(null);
        Assert.DoesNotContain(rows, r => r.IsAttached);
        Assert.False(vm.HasActiveScript);
        Assert.False(vm.CanAddToScript(rows[0])); // no script to add to
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
        Assert.Equal(["New Group", "billing"], reloaded.Nodes.Select(n => n.Name));
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
        Assert.Equal(["Top Level", "A", "A / B", "Other"], forConnection.Select(t => t.Path));
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
        Assert.True(vm.Find(vm.Nodes.Single(n => n.Name == "Another").Id, isConnection: false)!.IsExpanded);
    }

    [Fact]
    public async Task ADamagedGroupsFile_Should_ShowTheConnectionsAndNotBeOverwritten()
    {
        File.WriteAllText(GroupsPath, "{ damaged");
        var vm = await CreateAsync(Connection("billing"));

        Assert.True(vm.HasError);
        Assert.False(vm.CanEditGroups);
        Assert.Equal("billing", Assert.Single(vm.Nodes).Name);
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

// What a connection row says without connecting: missing secrets and variables, and the contract's
// state (being read, unreadable, out of date after a failed refresh), followed live as reads happen.
public sealed class ConnectionStatusTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("Invoklet-connection-status").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // Reads its contract only when the test lets it, failing or succeeding as told.
    private sealed class ContractDriver : IConnectionSchemaDriver
    {
        public TaskCompletionSource<ConnectionSchema> Next { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FetchStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "contract";
        public string DisplayName => "Contract";
        public ConnectionCategory Category => ConnectionCategory.Database;
        public IReadOnlyList<ConnectionParam> Params => [];
        public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string>();
        public IReadOnlyList<string> Validate(MdRunConnection connection) => [];
        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(true, ""));
        public ConnectionClientCode CreateClient(MdRunConnection connection, ConnectionCodeContext context) => new("");
        public Task<ConnectionSchema> FetchSchemaAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, ConnectionTools tools,
            CancellationToken cancellationToken)
        {
            FetchStarted.TrySetResult(true);
            return Next.Task.WaitAsync(cancellationToken);
        }
    }

    private (ConnectionNavigationViewModel Vm, ContractDriver Driver) Create(params MdRunConnection[] connections)
    {
        var store = new FileConnectionStore(Path.Combine(_folder, "connections.json"));
        store.Save(connections, null);
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new HttpConnectionDriver());
        var driver = new ContractDriver();
        drivers.Register(driver);
        var schemas = new ConnectionSchemas(drivers, Path.Combine(_folder, "schemas"),
            new ConnectionTools { GetPackageFolderAsync = (_, _, _) => Task.FromResult("") }, _ => null);
        var secrets = Mock.Of<ISecretStore>(s => s.LoadSnapshot() == new SecretStoreSnapshot(new[] { new Secret("token", "t0k") }, null));
        return (new ConnectionNavigationViewModel(store, drivers, secrets: secrets, schemas: schemas), driver);
    }

    private static MdRunConnection Http(string name, string authorization) => new()
    {
        Name = name,
        Driver = "http",
        Params = new(StringComparer.Ordinal)
        {
            ["baseAddress"] = MdRunValue.Of("https://example.test/"),
            ["headers"] = MdRunValue.Map([new("Authorization", MdRunValue.Of(authorization))]),
        },
    };

    // The work the rows wait for runs in the background and has nothing to await. A deadline on the clock, not a
    // count of rounds: two seconds of rounds was not enough when the whole suite ran at once, and costs nothing when met.
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    [Fact]
    public async Task MissingSecretsAndVariables_Should_BeProblems_WithoutReadingAnyValue()
    {
        var (vm, _) = Create(
            Http("known", "Bearer ${secret:token}"),
            Http("unknown", "Bearer ${secret:nope}"),
            Http("variable", "Bearer ${env:INVOKLET_TEST_SURELY_UNSET_VARIABLE}"));
        await vm.RefreshAsync();

        Assert.Equal(ConnectionRowState.Ready, vm.Find("known", isConnection: true)!.Status.State);
        Assert.Equal("The Secret Store has no secret named 'nope'. Add it in Tools → Secret Store.", vm.Find("unknown", isConnection: true)!.Problem);
        Assert.Equal("The environment variable 'INVOKLET_TEST_SURELY_UNSET_VARIABLE' is not set.", vm.Find("variable", isConnection: true)!.Problem);
    }

    [Fact]
    public async Task TheContractsState_Should_FollowItsReads_LoadingProblemReadyAndOutOfDate()
    {
        var (vm, driver) = Create(new MdRunConnection { Name = "orders", Driver = "contract" });
        await vm.RefreshAsync();
        var row = () => vm.Find("orders", isConnection: true)!;
        Assert.Equal(ConnectionRowState.Ready, row().Status.State);
        Assert.True(row().HasContract);

        var refresh = vm.RefreshConnectionAsync(row());
        await WaitUntil(() => row().IsLoading);
        Assert.Contains("reading its contract", row().AccessibleName);
        driver.Next.SetException(new InvalidOperationException("The server is down."));
        await refresh;
        await WaitUntil(() => row().HasProblem);
        Assert.Equal("Its contract couldn't be read: The server is down.", row().Problem);

        driver.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.Next.SetResult(new ConnectionSchema("2 services", [], ""));
        await vm.RefreshConnectionAsync(row());
        await WaitUntil(() => row().Status.State == ConnectionRowState.Ready);

        driver.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.Next.SetException(new InvalidOperationException("Timed out."));
        await vm.RefreshConnectionAsync(row());
        await WaitUntil(() => row().IsStale);
        Assert.Equal("Using the contract read earlier; the last refresh failed: Timed out.", row().StatusText);
        Assert.False(row().HasProblem); // the earlier contract still builds the script
    }

    [Fact]
    public async Task RefreshAll_Should_ReadTheListAndEveryContractAgain()
    {
        var (vm, driver) = Create(new MdRunConnection { Name = "orders", Driver = "contract" }, Http("known", "Bearer ${secret:token}"));
        await vm.RefreshAsync();
        driver.Next.SetException(new InvalidOperationException("Unreachable."));
        await vm.RefreshAllAsync();
        await WaitUntil(() => vm.Find("orders", isConnection: true)!.HasProblem);
        Assert.Equal("Its contract couldn't be read: Unreachable.", vm.Find("orders", isConnection: true)!.Problem);
        Assert.Equal(ConnectionRowState.Ready, vm.Find("known", isConnection: true)!.Status.State);
    }

    [Fact]
    public async Task ANewConnectionRefresh_ShouldCancelThePreviousOne_WithoutLosingTheCurrentOperation()
    {
        var (vm, driver) = Create(new MdRunConnection { Name = "orders", Driver = "contract" });
        await vm.RefreshAsync();
        var row = vm.Find("orders", isConnection: true)!;

        driver.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.FetchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = vm.RefreshConnectionAsync(row);
        await driver.FetchStarted.Task;

        // The second refresh cancels the first one's wait and waits for the same read: a schema
        // being read isn't read a second time (ConnectionSchemas.EnsureAsync).
        var second = vm.RefreshConnectionAsync(row);
        await first;

        driver.Next.SetResult(new ConnectionSchema("current", [], ""));
        await second;
        Assert.Equal(ConnectionRowState.Ready, row.Status.State);
    }
}

public sealed class SidebarStateTests
{
    private static ExampleScript Sample(string id, string folder) => new(id, id, "", MdRunLanguage.CSharp, MdRunKind.Statements, folder);

    [Fact]
    public void TheSamplesTree_Should_KeepTheBundledOrder_AndStartWithKeptFoldersFolded()
    {
        var changes = new List<(string, bool)>();
        var tree = SampleTreeNode.Build(
            [Sample("a.inv", "getting-started"), Sample("b.inv", "output"), Sample("c.inv", "getting-started"), Sample("top.inv", "")],
            ["output"], folder => changes.Add((folder.Id, folder.IsExpanded)));

        Assert.Equal(["Getting started", "Output", "top.inv"], tree.Select(n => n.Title));
        Assert.Equal(["a.inv", "c.inv"], tree[0].Children!.Select(n => n.Id));
        Assert.True(tree[0].IsExpanded);
        Assert.False(tree[1].IsExpanded);
        Assert.False(tree[2].IsFolder);
        Assert.Equal("Icon.LanguageCSharp", tree[2].IconKey);

        tree[1].IsExpanded = true;
        Assert.Equal([("output", true)], changes);
    }

    [Fact]
    public async Task FoldedGroups_Should_BeRestored_AndReportedForTheNextStart()
    {
        var folder = Directory.CreateTempSubdirectory("Invoklet-folded-groups").FullName;
        try
        {
            var store = new FileConnectionStore(Path.Combine(folder, "connections.json"));
            store.Save([new MdRunConnection { Name = "billing", Driver = "http" }], null);
            var first = new ConnectionNavigationViewModel(store);
            await first.RefreshAsync();
            var group = first.CreateGroup(null, "APIs")!;
            IReadOnlyCollection<string>? reported = null;
            first.CollapsedGroupsChanged += ids => reported = ids;
            first.Find(group.Id, isConnection: false)!.IsExpanded = false;
            Assert.Equal([group.Id], reported);

            var second = new ConnectionNavigationViewModel(store);
            second.RestoreCollapsedGroups(reported);
            await second.RefreshAsync();
            Assert.False(second.Find(group.Id, isConnection: false)!.IsExpanded);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
