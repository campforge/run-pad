using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Windows.Input;
using Moq;
using RunPad.Connectors.Http;
using RunPad.Contracts.Connections;
using RunPad.Format.RPad;
using RunPad.Services.Connections;
using RunPad.Services.Secrets;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.NonUI.Tests;

public sealed class FileConnectionStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("runpad-connections-tests").FullName;
    private string StorePath => Path.Combine(_folder, "nested", "connections.json");

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void SavedConnections_Should_LoadBack_WithTheirParams()
    {
        var store = new FileConnectionStore(StorePath);
        Assert.Empty(store.Load());
        store.Save(
        [
            new RpadConnection
            {
                Name = "billing",
                Driver = "http",
                Params = new(StringComparer.Ordinal)
                {
                    ["baseAddress"] = RpadValue.Of("https://billing.local/"),
                    ["headers"] = RpadValue.Map([new("Authorization", RpadValue.Of("Bearer ${secret:billing}"))]),
                },
            },
        ], null);
        var loaded = Assert.Single(new FileConnectionStore(StorePath).Load());
        Assert.Equal(("billing", "http"), (loaded.Name, loaded.Driver));
        Assert.Equal(["baseAddress", "headers"], loaded.Params.Keys);
        Assert.Equal("Bearer ${secret:billing}", loaded.Params["headers"].Fields!["Authorization"].Text);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StorePath));
    }

    [Theory]
    [InlineData("{ not json", "The connection list file is damaged.")]
    [InlineData("{\"Version\":2,\"Connections\":[]}", "The connection list was written by a newer RunPad (format 2).")]
    [InlineData("{\"Connections\":[]}", "The connection list file is damaged.")]
    [InlineData("{\"Version\":0,\"Connections\":[]}", "The connection list file is damaged.")]
    [InlineData("{\"Version\":1,\"Connections\":[null]}", "The connection list file is damaged.")]
    [InlineData("{\"Version\":1,\"Connections\":[{\"Name\":\"a\",\"Driver\":\"http\",\"Params\":null}]}", "The connection list file is damaged.")]
    [InlineData("{\"Version\":1,\"Connections\":[{\"Name\":\"a\",\"Driver\":\"http\",\"Params\":{\"headers\":{\"Fields\":{\"x\":null}}}}]}", "The connection list file is damaged.")]
    public void AListThatCantBeRead_Should_FailToLoad_AndStayUnchanged(string content, string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, content);
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => new FileConnectionStore(StorePath).Load()).Message);
        Assert.Equal(content, File.ReadAllText(StorePath));
    }
}

// The Connections window without a window: the form follows the driver's parameters, edits are
// saved only by Save (the window's OK), and Test uses the Secret Store's values.
public class ConnectionsWindowViewModelTests
{
    private sealed class FakeDriver : IConnectionDriver
    {
        public IReadOnlyDictionary<string, RpadValue>? Tested { get; private set; }
        public Exception? Failure { get; set; }
        // When set, a test waits for it: the server's answer comes when the test says so.
        public TaskCompletionSource<ConnectionTestResult>? Answer { get; set; }
        public string Id => "fake";
        public string DisplayName => "Fake";
        public ConnectionCategory Category => ConnectionCategory.Database;
        public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string>();

        public IReadOnlyList<ConnectionParam> Params { get; } =
        [
            new("connectionString", "Connection string", Required: true),
            new("pooling", "Pooling", ConnectionParamType.Boolean, Default: "true", Advanced: true),
            new("schemas", "Schemas", ConnectionParamType.List),
        ];

        public IReadOnlyList<string> Validate(RpadConnection connection) =>
            connection.Params.ContainsKey("connectionString") ? [] : ["The connection string is missing."];

        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, CancellationToken cancellationToken)
        {
            Tested = resolvedParams;
            if (Answer is not null) return Answer.Task;
            return Failure is null ? Task.FromResult(new ConnectionTestResult(true, "Connected.")) : Task.FromException<ConnectionTestResult>(Failure);
        }

        public ConnectionClientCode CreateClient(RpadConnection connection, ConnectionCodeContext context) =>
            new($"public sealed class {context.ClassName} {{ internal {context.ClassName}() {{ }} }}");
    }

    private static readonly RpadConnection Billing = new()
    {
        Name = "billing",
        Driver = "http",
        Params = new(StringComparer.Ordinal)
        {
            ["baseAddress"] = RpadValue.Of("https://billing.local/"),
            ["headers"] = RpadValue.Map([new("Authorization", RpadValue.Of("Bearer ${secret:billing}"))]),
            ["retries"] = RpadValue.Of("3"), // not an http parameter: kept as written
        },
    };

    private static readonly RpadConnection Future = new()
    {
        Name = "warehouse",
        Driver = "snowflake",
        Params = new(StringComparer.Ordinal) { ["account"] = RpadValue.Of("acme") },
    };

    private static (ConnectionsWindowViewModel Vm, Mock<IConnectionStore> Store, FakeDriver Fake) CreateVm(
        IReadOnlyList<Secret>? secrets = null, params RpadConnection[] connections)
    {
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.LoadSnapshot()).Returns(new ConnectionStoreSnapshot(connections, null));
        store.Setup(s => s.FilePath).Returns("/data/RunPad/connections.json");
        var secretStore = new Mock<ISecretStore>();
        secretStore.Setup(s => s.LoadSnapshot()).Returns(new SecretStoreSnapshot(secrets ?? [new Secret("billing", "t0k"), new Secret("db", "pw")], null));
        var registry = new ConnectionDriverRegistry();
        registry.Register(new HttpConnectionDriver());
        var fake = new FakeDriver();
        registry.Register(fake);
        return (new ConnectionsWindowViewModel(store.Object, registry, secretStore.Object), store, fake);
    }

    private static void Execute(ICommand command, object? parameter = null)
    {
        Assert.True(command.CanExecute(parameter));
        command.Execute(parameter);
    }

    [Fact]
    public void AConnection_Should_ShowAFieldPerDriverParameter()
    {
        var (vm, _, _) = CreateVm(null, Billing, Future);
        var billing = vm.SelectedConnection!;
        Assert.Equal("HTTP", billing.DriverName);
        Assert.Equal(["baseAddress", "headers", "timeout"], billing.Fields.Select(f => f.Param.Key));
        Assert.Equal("https://billing.local/", billing.Fields[0].Text);
        Assert.Equal(("Authorization", "Bearer ${secret:billing}"), (billing.Fields[1].Rows[0].Name, billing.Fields[1].Rows[0].Value));
        Assert.Equal("Also kept as written: retries.", billing.KeptParamsNote);
        Assert.Null(billing.Problems);

        var future = vm.Connections[1];
        Assert.Equal("snowflake (not available in this RunPad)", future.DriverName);
        Assert.Empty(future.Fields);
        Assert.False(((ICommand)vm.TestCommand).CanExecute(null) && vm.SelectedConnection == future);
    }

    [Fact]
    public void Edits_Should_BeSavedOnlyBySave_InTheDriversOrder()
    {
        var (vm, store, fake) = CreateVm(null, Billing, Future);
        Execute(vm.AddCommand, fake);
        var added = vm.SelectedConnection!;
        Assert.Equal("fake", added.Name);
        Assert.Equal(["connectionString", "schemas", "pooling"], added.Fields.Select(f => f.Param.Key)); // advanced last
        Assert.Equal("The connection string is missing.", added.Problems);

        added.Fields[0].SecretToInsert = "db";
        added.Fields[0].Text = "Data Source=x;Password=" + "${secret:db}";
        added.Fields[1].Text = "public\n\n sales \n";
        added.Fields[2].BooleanValue = false;
        Assert.Null(added.Problems);

        var billing = vm.Connections[0];
        Execute(billing.Fields[1].AddRowCommand);
        billing.Fields[1].Rows[1].Name = "Accept";
        billing.Fields[1].Rows[1].Value = "application/json";
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>()), Times.Never);

        IReadOnlyList<RpadConnection>? saved = null;
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>())).Callback<IReadOnlyList<RpadConnection>, string?>((list, _) => saved = list);
        Assert.True(vm.Save());

        Assert.NotNull(saved);
        Assert.Equal(["billing", "warehouse", "fake"], saved.Select(c => c.Name));
        Assert.Equal(["baseAddress", "headers", "retries"], saved[0].Params.Keys);
        Assert.Equal(["Authorization", "Accept"], saved[0].Params["headers"].Fields!.Keys);
        Assert.Equal("acme", saved[1].Params["account"].Text); // a driver this RunPad lacks: params kept
        Assert.Equal(["connectionString", "pooling", "schemas"], saved[2].Params.Keys);
        Assert.Equal("Data Source=x;Password=${secret:db}", saved[2].Params["connectionString"].Text);
        Assert.Equal("false", saved[2].Params["pooling"].Text);
        Assert.Equal(["public", "sales"], saved[2].Params["schemas"].Items!.Select(i => i.Text));
    }

    [Fact]
    public void ASwitchLeftAtItsDefault_AndEmptyFields_Should_NotBeWritten()
    {
        var (vm, store, fake) = CreateVm();
        Execute(vm.AddCommand, fake);
        vm.SelectedConnection!.Fields[0].Text = "Data Source=x";
        IReadOnlyList<RpadConnection>? saved = null;
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>())).Callback<IReadOnlyList<RpadConnection>, string?>((list, _) => saved = list);
        Assert.True(vm.Save());
        Assert.Equal(["connectionString"], Assert.Single(saved!).Params.Keys);
    }

    [Fact]
    public void BadNames_Should_BlockSaving_ButAnUnfinishedConnectionNot()
    {
        var (vm, store, fake) = CreateVm(null, Billing);
        Execute(vm.AddCommand, fake);
        Assert.True(vm.CanSave); // the connection string is still missing: a problem, not a block

        vm.SelectedConnection!.Name = "Billing";
        Assert.Equal("Two connections are named 'Billing'.", vm.ValidationError);
        Assert.False(vm.Save());
        vm.SelectedConnection.Name = "2nd";
        Assert.False(vm.CanSave);
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>()), Times.Never);

        Execute(vm.RemoveCommand);
        Assert.True(vm.CanSave);
        Assert.Equal("billing", vm.SelectedConnection?.Name);
    }

    [Fact]
    public void AddedNames_Should_NotRepeat()
    {
        var (vm, _, _) = CreateVm(null, Billing, new RpadConnection { Name = "http", Driver = "http" });
        var http = vm.Drivers.First(d => d.Id == "http");
        Execute(vm.AddCommand, http);
        Execute(vm.AddCommand, http);
        Assert.Equal(["billing", "http", "http2", "http3"], vm.Connections.Select(c => c.Name));
    }

    [Fact]
    public async Task Test_Should_UseTheSecretStore_AndReportMissingSecrets()
    {
        var (vm, _, fake) = CreateVm();
        Execute(vm.AddCommand, fake);
        var item = vm.SelectedConnection!;
        item.Fields[0].Text = "Password=${secret:DB}";
        await vm.TestCommand.Execute();
        Assert.Equal("Success: Connected.", item.TestResult);
        Assert.Equal("Password=pw", fake.Tested!["connectionString"].Text);

        item.Fields[0].Text = "Password=${secret:missing}";
        Assert.Null(item.TestResult); // an edit clears the last result
        await vm.TestCommand.Execute();
        Assert.Equal("The Secret Store has no secret named 'missing'. Add it in Tools → Secret Store.", item.TestResult);
    }

    // An edit while the server is still answering clears the result; the answer, about the values
    // before the edit, must not then appear under the new ones.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnEditDuringATest_Should_NotShowTheOldAnswer(bool succeeds)
    {
        var (vm, _, fake) = CreateVm();
        Execute(vm.AddCommand, fake);
        var item = vm.SelectedConnection!;
        item.Fields[0].Text = "Host=old";
        fake.Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var test = vm.TestCommand.Execute().ToTask();
        Assert.Equal("Testing…", item.TestResult);

        item.Fields[0].Text = "Host=new";
        if (succeeds) fake.Answer.SetResult(new ConnectionTestResult(true, "Connected to old."));
        else fake.Answer.SetException(new InvalidOperationException("old is down."));
        await test;

        Assert.Equal("Host=old", fake.Tested!["connectionString"].Text);
        Assert.Null(item.TestResult);
        Assert.False(vm.IsTesting);

        fake.Answer = null;
        await vm.TestCommand.Execute();
        Assert.Equal("Success: Connected.", item.TestResult);
        Assert.Equal("Host=new", fake.Tested!["connectionString"].Text);
    }

    // Drivers may throw for an unreachable server; the window reports it instead of crashing.
    [Fact]
    public async Task ADriverThatThrows_Should_FailTheTest_AndAllowAnother()
    {
        var (vm, _, fake) = CreateVm();
        Execute(vm.AddCommand, fake);
        var item = vm.SelectedConnection!;
        item.Fields[0].Text = "Host=nowhere";
        fake.Failure = new InvalidOperationException("No such host.");
        await vm.TestCommand.Execute();
        Assert.Equal("Failed: No such host.", item.TestResult);
        Assert.False(vm.IsTesting);
        Assert.True(((ICommand)vm.TestCommand).CanExecute(null));
    }

    // The list's status line belongs to its connection, so it still says what the test is doing after the
    // selection moves away.
    [Fact]
    public async Task TestSummary_Should_StayWithItsConnection_WhenAnotherIsSelected()
    {
        var (vm, _, fake) = CreateVm(null, Billing);
        Execute(vm.AddCommand, fake);
        var added = vm.SelectedConnection!;
        added.Fields[0].Text = "Host=slow";
        fake.Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var test = vm.TestCommand.Execute().ToTask();
        Assert.Equal("Testing…", added.TestSummary);

        vm.SelectedConnection = vm.Connections[0];
        Assert.Equal("Testing…", added.TestSummary);
        Assert.Null(vm.Connections[0].TestSummary);

        fake.Answer.SetResult(new ConnectionTestResult(true, "Connected."));
        await test;
        Assert.Equal("Test succeeded", added.TestSummary);
        Assert.Equal("Success: Connected.", added.TestResult);
    }

    [Fact]
    public async Task TestSummary_Should_ReportAFailure()
    {
        var (vm, _, fake) = CreateVm();
        Execute(vm.AddCommand, fake);
        var item = vm.SelectedConnection!;
        item.Fields[0].Text = "Host=nowhere";
        fake.Failure = new InvalidOperationException("No such host.");
        await vm.TestCommand.Execute();
        Assert.Equal("Test failed", item.TestSummary);
        item.Fields[0].Text = "Host=elsewhere";
        Assert.Null(item.TestSummary); // an edit clears the result, and its status with it
    }

    // A value without a name would be dropped silently on save, so it blocks saving until it is named or
    // removed; a new empty row stays ignorable.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHeaderWithAValueAndNoName_Should_BlockSave_UntilItIsNamedOrRemoved(bool named)
    {
        var (vm, store, _) = CreateVm(null, Billing);
        var headers = vm.Connections[0].Fields.Single(f => f.Param.Key == "headers");
        Execute(headers.AddRowCommand);
        Assert.Null(vm.ValidationError);
        headers.Rows[^1].Value = "orphaned";
        Assert.False(vm.CanSave);
        Assert.Contains("no name", vm.ValidationError);
        Assert.False(vm.Save());
        Assert.Contains("no name", vm.Connections[0].Problems);
        Assert.False(vm.CanTest);
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>()), Times.Never);

        if (named) headers.Rows[^1].Name = "X-Trace";
        else Execute(headers.RemoveRowCommand, headers.Rows[^1]);
        Assert.Null(vm.ValidationError);
        Assert.True(vm.Save());
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>()), Times.Once);
    }

    // An otherwise incomplete draft (a missing required parameter) still saves; only the test waits.
    [Fact]
    public void AnIncompleteDraft_Should_StillSave()
    {
        var (vm, store, _) = CreateVm(null, Billing);
        Execute(vm.AddCommand, vm.Drivers.First(d => d.Id == "fake"));
        Assert.Contains("connection string is missing", vm.SelectedConnection!.Problems);
        Assert.True(vm.Save());
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public void AConnectionWithProblems_Should_NotBeTested()
    {
        var (vm, _, fake) = CreateVm();
        Execute(vm.AddCommand, fake);
        var item = vm.SelectedConnection!;
        Assert.Equal("The connection string is missing.", item.Problems);
        Assert.False(vm.CanTest);
        Assert.False(((ICommand)vm.TestCommand).CanExecute(null));

        item.Fields[0].Text = "Host=db";
        Assert.True(vm.CanTest);
        item.Fields[0].Text = "Password=${secret:db";
        Assert.False(vm.CanTest);
    }

    [Fact]
    public void AListThatCantBeRead_Should_BeReported_AndNeverOverwritten()
    {
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.LoadSnapshot()).Throws(new InvalidDataException("The connection list file is damaged."));
        store.Setup(s => s.FilePath).Returns("/data/RunPad/connections.json");
        var vm = new ConnectionsWindowViewModel(store.Object, new ConnectionDriverRegistry(),
            Mock.Of<ISecretStore>(secrets => secrets.LoadSnapshot() == new SecretStoreSnapshot(Array.Empty<Secret>(), null)));
        Assert.Contains("damaged", vm.LoadError);
        Assert.False(vm.Save());
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public void HasChanges_Should_TrackTheDraftAgainstTheLoadedList()
    {
        var (vm, _, _) = CreateVm(null, Billing, Future);
        Assert.False(vm.HasChanges);

        vm.SelectedConnection = vm.Connections[1];
        vm.Connections[0].TestResult = "Success: Connected.";
        Assert.False(vm.HasChanges); // selection and test status are display only

        var billing = vm.Connections[0];
        var address = billing.Fields.Single(f => f.Param.Key == "baseAddress");
        var original = address.Text;
        address.Text = "https://billing.example/";
        Assert.True(vm.HasChanges);
        address.Text = original;
        Assert.False(vm.HasChanges); // reverted

        billing.Name = "invoices";
        Assert.True(vm.HasChanges);
        billing.Name = "billing";
        Assert.False(vm.HasChanges);

        var headers = billing.Fields.Single(f => f.Param.Key == "headers");
        Execute(headers.AddRowCommand);
        Assert.False(vm.HasChanges); // an empty row isn't written
        headers.Rows[^1].Name = "X-Trace";
        Assert.True(vm.HasChanges);
        Execute(headers.RemoveRowCommand, headers.Rows[^1]);
        Assert.False(vm.HasChanges);

        Execute(vm.AddCommand, vm.Drivers.First(d => d.Id == "fake"));
        Assert.True(vm.HasChanges);
        Execute(vm.RemoveCommand);
        Assert.False(vm.HasChanges);

        vm.SelectedConnection = vm.Connections[0];
        Execute(vm.RemoveCommand);
        Assert.True(vm.HasChanges);
    }

    [Fact]
    public void HasChanges_Should_ResetAfterASuccessfulSave_AndStayAfterAFailedOne()
    {
        var (vm, store, _) = CreateVm(null, Billing);
        vm.Connections[0].Name = "invoices";
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>())).Throws(new IOException());
        Assert.False(vm.Save());
        Assert.True(vm.HasChanges);

        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>())).Returns("saved");
        Assert.True(vm.Save());
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void AnUnreadableList_Should_NotBeDirty()
    {
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.LoadSnapshot()).Throws(new InvalidDataException("damaged."));
        store.Setup(s => s.FilePath).Returns("/data/RunPad/connections.json");
        var vm = new ConnectionsWindowViewModel(store.Object, new ConnectionDriverRegistry(),
            Mock.Of<ISecretStore>(secrets => secrets.LoadSnapshot() == new SecretStoreSnapshot(Array.Empty<Secret>(), null)));
        Assert.False(vm.HasChanges);
    }
}
