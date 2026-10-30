using System.Reflection;
using System.Windows.Input;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Invoklet.Connectors.Http;
using Invoklet.Contracts.Connections;
using Invoklet.Format.MdRun;
using Invoklet.Services.Connections;
using Invoklet.Services.Secrets;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.Windows;

public class ConnectionsWindowTests
{
    private sealed class SchemaDriver : IConnectionSchemaDriver
    {
        public string Id => "sqlite";
        public string DisplayName => "SQLite";
        public ConnectionCategory Category => ConnectionCategory.Database;
        public IReadOnlyList<ConnectionParam> Params => [];
        public IReadOnlyDictionary<string, string> Packages => new Dictionary<string, string>();
        public IReadOnlyList<string> Validate(MdRunConnection connection) => [];
        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(true, "Connected."));
        public ConnectionClientCode CreateClient(MdRunConnection connection, ConnectionCodeContext context) => new("");
        public Task<ConnectionSchema> FetchSchemaAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, ConnectionTools tools,
            CancellationToken cancellationToken) => Task.FromResult(new ConnectionSchema("1 table", [], "[]"));
    }

    [AvaloniaFact]
    public void RightPane_Should_ShowCachedDatabaseResourcesAndTheirDetails()
    {
        var connection = new MdRunConnection { Name = "warehouse", Driver = "sqlite" };
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.LoadSnapshot()).Returns(new ConnectionStoreSnapshot([connection], null));
        store.Setup(s => s.FilePath).Returns("/data/connections.json");
        var secrets = new Mock<ISecretStore>();
        secrets.Setup(s => s.LoadSnapshot()).Returns(new SecretStoreSnapshot([], null));
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new SchemaDriver());
        var schemaRoot = Directory.CreateTempSubdirectory("Invoklet-connection-browser");
        try
        {
            var schemas = new ConnectionSchemas(drivers, schemaRoot.FullName,
                new ConnectionTools { GetPackageFolderAsync = (_, _, _) => Task.FromResult("") }, _ => null);
            schemas.Cache.Put(connection, new ConnectionSchema("1 table", [],
                "[{\"Name\":\"Orders\",\"IsView\":false,\"Columns\":[{\"Name\":\"OrderId\",\"Type\":\"INTEGER\",\"NotNull\":true,\"PrimaryKey\":1}]}]"));
            var vm = new ConnectionsWindowViewModel(store.Object, drivers, secrets.Object, schemas);
            var owner = new Window();
            owner.Show();
            var window = new ConnectionsWindow(vm);
            window.ShowDialog<bool>(owner);
            Dispatcher.UIThread.RunJobs();

            var tree = window.FindControl<TreeView>("ResourceTree")!;
            Assert.Single(vm.Resources);
            Assert.Equal("Orders", vm.Resources[0].Name);
            Assert.Equal("OrderId", Assert.Single(vm.Resources[0].Children).Name);
            Assert.Equal("Database table", vm.ResourceDetails[2].Value);
            Assert.Equal("INTEGER · not null · primary key", vm.Resources[0].Children[0].Details);
            Assert.NotNull(tree);
            window.Close();
            owner.Close();
        }
        finally { schemaRoot.Delete(true); }
    }

    [AvaloniaFact]
    public async Task TheForm_Should_FollowTheDriver_AndOkShouldSaveTheEdits()
    {
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.LoadSnapshot()).Returns(new ConnectionStoreSnapshot(
        [
            new MdRunConnection
            {
                Name = "billing",
                Driver = "http",
                Params = new(StringComparer.Ordinal)
                {
                    ["baseAddress"] = MdRunValue.Of("https://billing.local/"),
                    ["headers"] = MdRunValue.Map([new("Accept", MdRunValue.Of("application/json"))]),
                },
            },
        ], null));
        store.Setup(s => s.FilePath).Returns("/data/Invoklet/connections.json");
        IReadOnlyList<MdRunConnection>? saved = null;
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<MdRunConnection>>(), It.IsAny<string?>())).Callback<IReadOnlyList<MdRunConnection>, string?>((list, _) => saved = list);
        var secrets = new Mock<ISecretStore>();
        secrets.Setup(s => s.LoadSnapshot()).Returns(new SecretStoreSnapshot([new Secret("billing-token", "t0k")], null));
        var registry = new ConnectionDriverRegistry();
        registry.Register(new HttpConnectionDriver());

        var owner = new Window();
        owner.Show();
        var window = new ConnectionsWindow(new ConnectionsWindowViewModel(store.Object, registry, secrets.Object));
        var result = window.ShowDialog<bool>(owner);
        Dispatcher.UIThread.RunJobs();

        // One Add entry per driver.
        var menu = (MenuFlyout)window.FindControl<DropDownButton>("AddButton")!.Flyout!;
        Assert.Equal(["HTTP"], menu.Items.Cast<MenuItem>().Select(i => i.Header));

        // Base address, headers and timeout, each with its own editor.
        var fields = window.FindControl<ItemsControl>("FieldList")!;
        Assert.Equal(3, fields.ItemCount);
        var addressBox = fields.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("field"));
        Assert.Equal("https://billing.local/", addressBox.Text);
        addressBox.Text = "https://billing.example/";
        Dispatcher.UIThread.RunJobs();

        // A row's remove button is an icon, so it names the row in its tooltip, as in its accessible name.
        var removeRow = fields.GetVisualDescendants().OfType<Button>().Single(b => b.Content is PathIcon);
        var row = (ConnectionMapRowViewModel)removeRow.DataContext!;
        Assert.Equal(row.RemoveRowLabel, ToolTip.GetTip(removeRow));
        Assert.Equal(row.RemoveRowLabel, AutomationProperties.GetName(removeRow));
        Assert.EndsWith("Accept", row.RemoveRowLabel);

        window.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(await result);
        Assert.Equal("https://billing.example/", Assert.Single(saved!).Params["baseAddress"].Text);
        owner.Close();
    }
    [AvaloniaFact]
    public async Task SaveFailure_Should_KeepDialogOpenWithEdits_AndAllowRetry()
    {
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.LoadSnapshot()).Returns(new ConnectionStoreSnapshot([], null));
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<MdRunConnection>>(), It.IsAny<string?>()))
            .Throws(new IOException("Private connection text must not appear"));
        var secrets = new Mock<ISecretStore>();
        secrets.Setup(s => s.LoadSnapshot()).Returns(new SecretStoreSnapshot([], null));
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new HttpConnectionDriver());
        var vm = new ConnectionsWindowViewModel(store.Object, drivers, secrets.Object);
        ((System.Windows.Input.ICommand)vm.AddCommand).Execute(drivers.Find("http"));
        vm.Connections[0].Name = "draft";
        var owner = new Window();
        owner.Show();
        var window = new ConnectionsWindow(vm);
        var result = window.ShowDialog<bool>(owner);
        try
        {
            Dispatcher.UIThread.RunJobs();
            var ok = window.FindControl<Button>("OkButton")!;
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(result.IsCompleted);
            Assert.Equal("draft", vm.Connections[0].Name);
            var error = window.FindControl<TextBlock>("StoreError")!;
            Assert.True(error.IsVisible);
            Assert.Contains("Could not save", error.Text);
            Assert.DoesNotContain("Private connection", error.Text);
            store.Setup(s => s.Save(It.IsAny<IReadOnlyList<MdRunConnection>>(), It.IsAny<string?>())).Returns("saved");
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(await result);
        }
        finally
        {
            window.Close();
            owner.Close();
        }
    }

    private static (ConnectionsWindow Window, Mock<IConnectionStore> Store, Task<bool> Result, Window Owner) Open(bool withConnection = true)
    {
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.LoadSnapshot()).Returns(new ConnectionStoreSnapshot(withConnection
            ? [new MdRunConnection { Name = "billing", Driver = "http", Params = new(StringComparer.Ordinal) { ["baseAddress"] = MdRunValue.Of("https://billing.local/") } }]
            : [], null));
        store.Setup(s => s.FilePath).Returns("/data/Invoklet/connections.json");
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<MdRunConnection>>(), It.IsAny<string?>())).Returns("saved");
        var secrets = new Mock<ISecretStore>();
        secrets.Setup(s => s.LoadSnapshot()).Returns(new SecretStoreSnapshot([], null));
        var registry = new ConnectionDriverRegistry();
        registry.Register(new HttpConnectionDriver());
        var owner = new Window();
        owner.Show();
        var window = new ConnectionsWindow(new ConnectionsWindowViewModel(store.Object, registry, secrets.Object));
        var result = window.ShowDialog<bool>(owner);
        Dispatcher.UIThread.RunJobs();
        return (window, store, result, owner);
    }

    // Cancel, Escape and the native close button, each ending in the Save / Don't Save / Cancel question.
    public enum CloseVia { CancelButton, Escape, NativeClose }

    private static void RequestClose(Window window, CloseVia via)
    {
        switch (via)
        {
            case CloseVia.CancelButton:
                window.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                break;
            case CloseVia.Escape:
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                break;
            default:
                // The windowing backend's close request (the title bar's close button), as opposed to Window.Close().
                var impl = window.PlatformImpl!;
                var closing = (Func<WindowCloseReason, bool>?)impl.GetType().GetProperty("Closing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(impl);
                closing?.Invoke(WindowCloseReason.WindowClosing);
                break;
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Choose(Window window, string button)
    {
        var dialog = Assert.IsType<UnsavedChangesDialog>(Assert.Single(window.OwnedWindows));
        var target = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, button));
        target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(CloseVia.CancelButton)]
    [InlineData(CloseVia.Escape)]
    [InlineData(CloseVia.NativeClose)]
    public async Task Closing_Should_CloseAtOnce_WhenNothingChanged(CloseVia via)
    {
        var (window, store, result, owner) = Open();
        try
        {
            RequestClose(window, via);
            Assert.Empty(window.OwnedWindows);
            Assert.False(await result);
            store.Verify(s => s.Save(It.IsAny<IReadOnlyList<MdRunConnection>>(), It.IsAny<string?>()), Times.Never);
        }
        finally { owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(CloseVia.CancelButton, "Save", true, 1)]
    [InlineData(CloseVia.Escape, "Don’t Save", false, 0)]
    [InlineData(CloseVia.NativeClose, "Save", true, 1)]
    [InlineData(CloseVia.NativeClose, "Don’t Save", false, 0)]
    public async Task Closing_Should_AskAboutChanges_AndHonourTheAnswer(CloseVia via, string answer, bool saved, int writes)
    {
        var (window, store, result, owner) = Open();
        try
        {
            ((ConnectionsWindowViewModel)window.DataContext!).Connections[0].Name = "invoices";
            RequestClose(window, via);
            Assert.False(result.IsCompleted);
            Choose(window, answer);
            Assert.Equal(saved, await result);
            store.Verify(s => s.Save(It.IsAny<IReadOnlyList<MdRunConnection>>(), It.IsAny<string?>()), Times.Exactly(writes));
        }
        finally { owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(CloseVia.CancelButton)]
    [InlineData(CloseVia.NativeClose)]
    public async Task CancellingTheQuestion_Should_KeepTheWindowAndDraft_AndAskOnlyOnce(CloseVia via)
    {
        var (window, store, result, owner) = Open();
        try
        {
            ((ConnectionsWindowViewModel)window.DataContext!).Connections[0].Name = "invoices";
            RequestClose(window, via);
            RequestClose(window, via); // while the question is up: no second one
            Assert.Single(window.OwnedWindows);
            Choose(window, "Cancel");
            Assert.False(result.IsCompleted);
            Assert.Empty(window.OwnedWindows);
            Assert.True(((ConnectionsWindowViewModel)window.DataContext!).HasChanges);
            store.Verify(s => s.Save(It.IsAny<IReadOnlyList<MdRunConnection>>(), It.IsAny<string?>()), Times.Never);

            RequestClose(window, via); // still asks the next time
            Choose(window, "Don’t Save");
            Assert.False(await result);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task SaveFromTheQuestion_Should_KeepTheWindowOpen_WhenSavingFails()
    {
        var (window, store, result, owner) = Open();
        try
        {
            ((ConnectionsWindowViewModel)window.DataContext!).Connections[0].Name = "invoices";
            store.Setup(s => s.Save(It.IsAny<IReadOnlyList<MdRunConnection>>(), It.IsAny<string?>())).Throws(new IOException("private text"));
            RequestClose(window, CloseVia.CancelButton);
            Choose(window, "Save");
            Assert.False(result.IsCompleted);
            Assert.True(((ConnectionsWindowViewModel)window.DataContext!).HasChanges);

            store.Setup(s => s.Save(It.IsAny<IReadOnlyList<MdRunConnection>>(), It.IsAny<string?>())).Returns("saved");
            RequestClose(window, CloseVia.CancelButton);
            Choose(window, "Save");
            Assert.True(await result);
        }
        finally { owner.Close(); }
    }

    // A header with a value and no name is an unsaved change: closing asks, and Save refuses it until it is named.
    [AvaloniaTheory]
    [InlineData(CloseVia.CancelButton)]
    [InlineData(CloseVia.NativeClose)]
    public async Task AHeaderWithAValueAndNoName_Should_AskBeforeClosing_AndStayOpenUntilNamed(CloseVia via)
    {
        var (window, store, result, owner) = Open();
        try
        {
            var vm = (ConnectionsWindowViewModel)window.DataContext!;
            var headers = vm.Connections[0].Fields.Single(f => f.Param.Key == "headers");
            ((ICommand)headers.AddRowCommand).Execute(null);
            var row = headers.Rows[^1];
            row.Value = "orphaned";
            Dispatcher.UIThread.RunJobs();
            var rowError = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == row.NameError);
            Assert.True(rowError.IsEffectivelyVisible);

            RequestClose(window, via);
            Choose(window, "Save");
            Assert.False(result.IsCompleted);
            store.Verify(s => s.Save(It.IsAny<IReadOnlyList<MdRunConnection>>(), It.IsAny<string?>()), Times.Never);

            row.Name = "X-Trace";
            Dispatcher.UIThread.RunJobs();
            Assert.False(rowError.IsEffectivelyVisible);
            RequestClose(window, via);
            Choose(window, "Save");
            Assert.True(await result);
            store.Verify(s => s.Save(It.IsAny<IReadOnlyList<MdRunConnection>>(), It.IsAny<string?>()), Times.Once);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task OwnerShutdown_Should_NotAsk()
    {
        var (window, _, result, owner) = Open();
        ((ConnectionsWindowViewModel)window.DataContext!).Connections[0].Name = "invoices";
        owner.Close();
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(window.OwnedWindows);
        Assert.False(await result);
    }
}
