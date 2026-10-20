using System.Windows.Input;
using Moq;
using RunPad.Connectors.Http;
using RunPad.Contracts;
using RunPad.Contracts.Connections;
using RunPad.Format.RPad;
using RunPad.Services.Connections;
using RunPad.Services.Secrets;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.NonUI.Tests;

// Dependencies → Connections: adding the user's connections to a script, removing them, importing a
// script's connection into the user's list, and what each one's status says.
public class ScriptConnectionsViewModelTests
{
    private static RpadConnection Http(string name, string address, string? token = null) => new()
    {
        Name = name,
        Driver = "http",
        Params = token is null
            ? new(StringComparer.Ordinal) { ["baseAddress"] = RpadValue.Of(address) }
            : new(StringComparer.Ordinal)
            {
                ["baseAddress"] = RpadValue.Of(address),
                ["headers"] = RpadValue.Map([new("Authorization", RpadValue.Of($"Bearer ${{secret:{token}}}"))]),
            },
    };

    private sealed class SqlLikeDriver : IConnectionDriver
    {
        public string Id => "sqlish";
        public string DisplayName => "SQL-ish";
        public ConnectionCategory Category => ConnectionCategory.Database;
        public IReadOnlyList<ConnectionParam> Params { get; } = [];
        public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string> { ["Microsoft.Data.Sqlite"] = "9.0.0" };
        public IReadOnlyList<string> Validate(RpadConnection connection) => [];
        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(true, ""));
        public ConnectionClientCode CreateClient(RpadConnection connection, ConnectionCodeContext context) => new("");
    }

    private static (DependenciesWindowViewModel Vm, Mock<IConnectionStore> Store, List<RpadConnection> Mine) CreateVm(params RpadConnection[] mine)
    {
        var list = mine.ToList();
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.LoadSnapshot()).Returns(() => new ConnectionStoreSnapshot([.. list], null));
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>()))
            .Callback<IReadOnlyList<RpadConnection>, string?>((saved, _) => { list.Clear(); list.AddRange(saved); });
        var secrets = new Mock<ISecretStore>();
        secrets.Setup(s => s.LoadSnapshot()).Returns(new SecretStoreSnapshot([new Secret("billing-token", "t0k")], null));
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new HttpConnectionDriver());
        drivers.Register(new SqlLikeDriver());
        var sources = new Mock<INuGetSourceStore>();
        sources.Setup(s => s.Load()).Returns([]);
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Returns([]);
        var vm = new DependenciesWindowViewModel(Mock.Of<IPackageSearchService>(), cache.Object, sources.Object, TimeSpan.Zero,
            store.Object, secrets.Object, drivers);
        return (vm, store, list);
    }

    private static void Execute(ICommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
    }

    [Fact]
    public void AddingYourConnection_Should_PutItInTheScript_OnSave()
    {
        var billing = Http("billing", "https://billing.local/", "billing-token");
        var (vm, _, _) = CreateVm(billing, Http("github", "https://api.github.com/"));
        var fm = new RpadFrontMatter { Connections = [Http("billing", "https://billing.local/", "billing-token")] };
        vm.SetFrontMatter(fm);
        var connections = vm.Connections;

        var item = Assert.Single(connections.Items);
        Assert.Equal("Connections.Billing", item.CodeName);
        Assert.Equal("HTTP", item.DriverName);
        Assert.Equal("Same as in your connections.", item.Status);
        Assert.Null(item.Problem);
        Assert.Equal(["github"], connections.Available.Select(c => c.Name)); // billing is already in the script
        Assert.False(vm.HasChanges);

        Execute(connections.AddCommand);
        Assert.Equal(["billing", "github"], connections.Items.Select(i => i.Name));
        Assert.Empty(connections.Available);
        Assert.True(vm.HasChanges);

        Assert.True(vm.ApplyChanges());
        Assert.Equal(["billing", "github"], fm.Connections.Select(c => c.Name));
        Assert.Equal("https://api.github.com/", fm.Connections[1].Params["baseAddress"].Text);
    }

    [Fact]
    public void Removing_Should_TakeTheConnectionOutOfTheScript()
    {
        var (vm, _, _) = CreateVm();
        var fm = new RpadFrontMatter { Connections = [Http("a", "https://a/"), Http("b", "https://b/")] };
        vm.SetFrontMatter(fm);
        vm.Connections.SelectedItem = vm.Connections.Items[0];
        Execute(vm.Connections.RemoveCommand);
        Assert.Equal("b", vm.Connections.SelectedItem?.Name);
        Assert.True(vm.ApplyChanges());
        Assert.Equal(["b"], fm.Connections.Select(c => c.Name));
    }

    [Fact]
    public void Import_Should_KeepTheScriptsConnectionInYours_WithoutChangingTheScript()
    {
        var (vm, store, mine) = CreateVm(Http("billing", "https://old.billing/"));
        vm.SetFrontMatter(new RpadFrontMatter { Connections = [Http("billing", "https://billing.local/"), Http("orders", "https://orders/")] });
        Assert.Equal("Differs from your connection of the same name: Import replaces yours.", vm.Connections.Items[0].Status);
        Assert.Equal("Not in your connections: Import keeps it there.", vm.Connections.Items[1].Status);

        vm.Connections.SelectedItem = vm.Connections.Items[0];
        Execute(vm.Connections.ImportCommand);
        vm.Connections.SelectedItem = vm.Connections.Items[1];
        Execute(vm.Connections.ImportCommand);

        Assert.Equal(["billing", "orders"], mine.Select(c => c.Name));
        Assert.Equal("https://billing.local/", mine[0].Params["baseAddress"].Text);
        Assert.All(vm.Connections.Items, i => Assert.Equal("Same as in your connections.", i.Status));
        Assert.False(vm.HasChanges);
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>()), Times.Exactly(2));
    }

    [Fact]
    public void Problems_Should_SayWhatWouldFailTheBuild()
    {
        var (vm, _, _) = CreateVm();
        vm.SetFrontMatter(new RpadFrontMatter
        {
            Connections =
            [
                Http("billing", "https://billing.local/", "missing-token"),
                new RpadConnection { Name = "warehouse", Driver = "snowflake" },
                new RpadConnection { Name = "broken", Driver = "http" },
            ],
        });
        Assert.Equal(
            ["The Secret Store has no secret named 'missing-token'.", "This RunPad has no 'snowflake' driver.", "The base address is missing."],
            vm.Connections.Items.Select(i => i.Problem));

        vm.SetFrontMatter(new RpadFrontMatter { Language = RpadLanguage.Vb, Connections = [Http("a", "https://a/")] });
        Assert.Equal("Connections can be used in C# scripts only, for now.", Assert.Single(vm.Connections.Items).Problem);
    }

    // The libraries a connection needs come as the script's own packages, visible on the Packages tab.
    [Fact]
    public void AddingAConnection_Should_AddItsDriversPackages_AndAMissingOneIsAProblem()
    {
        var (vm, _, _) = CreateVm(new RpadConnection { Name = "local", Driver = "sqlish" });
        var fm = new RpadFrontMatter();
        vm.SetFrontMatter(fm);
        Execute(vm.Connections.AddCommand);
        Assert.Null(vm.Connections.Items.Single().Problem);
        Assert.True(vm.ApplyChanges());
        Assert.Equal("9.0.0", fm.Packages["Microsoft.Data.Sqlite"].Version);

        var (other, _, _) = CreateVm();
        other.SetFrontMatter(new RpadFrontMatter { Connections = [new RpadConnection { Name = "local", Driver = "sqlish" }] });
        Assert.Equal("Needs the NuGet package Microsoft.Data.Sqlite: add it on the Packages tab.", other.Connections.Items.Single().Problem);
    }
}
