using System.Reactive.Linq;
using System.Windows.Input;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RunPad.Connectors.Http;
using RunPad.Contracts.Connections;
using RunPad.Format.RPad;
using RunPad.Services.Connections;
using RunPad.Services.Secrets;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.NonUI.Tests;

public sealed class ConnectionSafetyTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("runpad-connection-safety-").FullName;
    private string StorePath => Path.Combine(_folder, "connections.json");
    public void Dispose() => Directory.Delete(_folder, true);

    private static RpadConnection Http(string name) => new()
    {
        Name = name,
        Driver = "http",
        Params = new() { ["baseAddress"] = RpadValue.Of("https://example.test/") },
    };

    private static ConnectionDriverRegistry Drivers()
    {
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new HttpConnectionDriver());
        return drivers;
    }

    private ConnectionsWindowViewModel Window(FileConnectionStore store) =>
        new(store, Drivers(), new FileSecretStore(Path.Combine(_folder, "secrets.json")));

    [Fact]
    public async Task DuplicateHeaders_Should_KeepBothDraftRows_AndBlockSaveAndTestUntilCorrected()
    {
        var store = new FileConnectionStore(StorePath);
        store.Save([Http("api")], null);
        var before = File.ReadAllBytes(StorePath);
        var vm = Window(store);
        var headers = vm.Connections[0].Fields.Single(f => f.IsMap);
        await headers.AddRowCommand.Execute();
        await headers.AddRowCommand.Execute();
        headers.Rows[0].Name = "Authorization";
        headers.Rows[0].Value = "first";
        headers.Rows[1].Name = " authorization ";
        headers.Rows[1].Value = "second";
        Assert.False(vm.CanSave);
        Assert.False(((ICommand)vm.TestCommand).CanExecute(null));
        Assert.Contains("unique", vm.ValidationError);
        Assert.False(vm.Save());
        Assert.Equal(before, File.ReadAllBytes(StorePath));
        Assert.Equal(2, headers.Rows.Count);
        headers.Rows[1].Name = "X-Other";
        Assert.True(vm.CanSave);
        Assert.True(((ICommand)vm.TestCommand).CanExecute(null));
        Assert.True(vm.Save());
        Assert.Equal("second", store.Load()[0].Params["headers"].Fields!["X-Other"].Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StaleWindows_Should_NotOverwriteNewerOrNewlyCreatedConnections(bool existing)
    {
        var store = new FileConnectionStore(StorePath);
        if (existing) store.Save([Http("original")], null);
        var a = Window(store);
        var b = Window(new FileConnectionStore(StorePath));
        if (!existing)
        {
            ((ICommand)a.AddCommand).Execute(Drivers().Find("http"));
            ((ICommand)b.AddCommand).Execute(Drivers().Find("http"));
        }
        a.Connections[0].Name = "newer";
        Assert.True(a.Save());
        var before = File.ReadAllBytes(StorePath);
        b.Connections[0].Name = "stale";
        Assert.False(b.Save());
        Assert.Contains("changed", b.SaveError);
        Assert.Equal("stale", b.Connections[0].Name);
        Assert.Equal(before, File.ReadAllBytes(StorePath));
        // A window advances its own version after saving.
        a.Connections[0].Name = "newest";
        Assert.True(a.Save());
        Assert.Equal("newest", store.Load()[0].Name);
    }

    [Fact]
    public void DiskFailure_Should_RetainDraftAndAllowRetry()
    {
        var store = new FileConnectionStore(StorePath);
        var vm = Window(store);
        ((ICommand)vm.AddCommand).Execute(Drivers().Find("http"));
        vm.Connections[0].Name = "draft";
        Directory.CreateDirectory(StorePath);
        Assert.False(vm.Save());
        Assert.NotNull(vm.SaveError);
        Assert.Equal("draft", vm.Connections[0].Name);
        Directory.Delete(StorePath);
        Assert.True(vm.Save());
        Assert.Null(vm.SaveError);
        Assert.Equal("draft", store.Load()[0].Name);
    }

    [Fact]
    public void WriterLock_Should_PreventReplacement_AndRetainItsInode()
    {
        var store = new FileConnectionStore(StorePath);
        var version = store.Save([Http("original")], null);
        var before = File.ReadAllBytes(StorePath);
        using (var writer = new FileStream(StorePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => store.Save([Http("replacement")], version));
        Assert.Equal(before, File.ReadAllBytes(StorePath));
        Assert.True(File.Exists(StorePath + ".lock"));
        store.Save([Http("replacement")], version);
        Assert.Equal("replacement", store.Load()[0].Name);
    }

    [Fact]
    public void DeletedStore_Should_NotBeRecreatedByStaleWindow()
    {
        var store = new FileConnectionStore(StorePath);
        store.Save([Http("original")], null);
        var vm = Window(store);
        File.Delete(StorePath);
        Assert.False(vm.Save());
        Assert.Contains("changed", vm.SaveError);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void ClientTypeNames_Should_NotCollideWithProperties_AndGeneratedCodeShouldCompile()
    {
        var generated = GeneratedConnections.For([Http("api"), Http("api-client"), Http("api-client2")], RpadLanguage.CSharp, [], Drivers())!;
        Assert.Empty(generated.Errors);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("connection-names", [CSharpSyntaxTree.ParseText(generated.Source, cancellationToken: TestContext.Current.CancellationToken)], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void EnclosingClassName_Should_BeReportedBeforeCompiling()
    {
        var generated = GeneratedConnections.For([Http("connections")], RpadLanguage.CSharp, [], Drivers())!;
        Assert.Contains(generated.Errors, e => e.Contains("reserved", StringComparison.Ordinal));
    }

    [Fact]
    public void ReplacingAnImportedConnection_Should_RequireConfirmation_AndRecheckTheStoredVersion()
    {
        var store = new FileConnectionStore(StorePath);
        store.Save([Http("api")], null);
        var changed = Http("api");
        changed.Params["baseAddress"] = RpadValue.Of("https://changed.test/");
        var vm = new ScriptConnectionsViewModel(store, new FileSecretStore(Path.Combine(_folder, "secrets.json")), Drivers());
        vm.Load(new RpadFrontMatter { Connections = [changed] });
        vm.SelectedItem = vm.Items[0];
        ((ICommand)vm.ImportCommand).Execute(null);
        Assert.True(vm.IsImportConfirmationPending);
        Assert.Equal("https://example.test/", store.Load()[0].Params["baseAddress"].Text);
        ((ICommand)vm.CancelImportCommand).Execute(null);
        Assert.False(vm.IsImportConfirmationPending);
        Assert.False(vm.HasChanges);
        ((ICommand)vm.ImportCommand).Execute(null);
        var latest = store.LoadSnapshot();
        store.Save([Http("newer")], latest.Version);
        ((ICommand)vm.ConfirmImportCommand).Execute(null);
        Assert.False(vm.IsImportConfirmationPending);
        Assert.Contains("changed", vm.StoreError);
        Assert.Equal("newer", store.Load()[0].Name);
        Assert.False(vm.HasChanges);
    }
}
