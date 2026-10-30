using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Invoklet.Connectors.Http;
using Invoklet.Contracts;
using Invoklet.Contracts.Connections;
using Invoklet.Format.MdRun;
using Invoklet.Services.Connections;
using Invoklet.Services.Secrets;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.Windows;

// Dependencies → Connections offers New Connection and Manage Connections: both open the same
// Connections window as Tools → Connections over the Dependencies window, whose draft is left alone.
public class DependenciesConnectionsEditorTests
{
    private sealed class PackageDriver : IConnectionDriver
    {
        public string Id => "package";
        public string DisplayName => "Package";
        public ConnectionCategory Category => ConnectionCategory.Database;
        public IReadOnlyList<ConnectionParam> Params { get; } = [];
        public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string> { ["Package.Client"] = "1.0.0" };
        public IReadOnlyList<string> Validate(MdRunConnection connection) => [];
        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(true, ""));
        public ConnectionClientCode CreateClient(MdRunConnection connection, ConnectionCodeContext context) => new("");
    }

    private sealed class Fixture
    {
        public List<MdRunConnection> Mine { get; } = [];
        public ConnectionDriverRegistry Drivers { get; } = new();
        public Mock<IConnectionStore> Store { get; } = new();
        public Mock<ISecretStore> Secrets { get; } = new();
        public ConnectionsWindow? Opened { get; private set; }

        public Fixture()
        {
            Drivers.Register(new HttpConnectionDriver());
            Store.Setup(s => s.LoadSnapshot()).Returns(() => new ConnectionStoreSnapshot([.. Mine], null));
            Store.Setup(s => s.FilePath).Returns("/data/Invoklet/connections.json");
            Store.Setup(s => s.Save(It.IsAny<IReadOnlyList<MdRunConnection>>(), It.IsAny<string?>()))
                .Callback<IReadOnlyList<MdRunConnection>, string?>((saved, _) => { Mine.Clear(); Mine.AddRange(saved); });
            Secrets.Setup(s => s.LoadSnapshot()).Returns(new SecretStoreSnapshot([], null));
        }

        public (DependenciesWindow Window, DependenciesWindowViewModel Vm, Window Owner) CreateWindow()
        {
            var sources = new Mock<INuGetSourceStore>();
            sources.Setup(s => s.Load()).Returns([]);
            var cache = new Mock<ILocalPackageCacheService>();
            cache.Setup(c => c.GetCachedPackages()).Returns([]);
            var vm = new DependenciesWindowViewModel(Mock.Of<IPackageSearchService>(), cache.Object, sources.Object, TimeSpan.Zero,
                Store.Object, Secrets.Object, Drivers);
            vm.SetFrontMatter(new MdRunFrontMatter());
            var window = new DependenciesWindow(vm, () =>
                Opened = new ConnectionsWindow(new ConnectionsWindowViewModel(Store.Object, Drivers, Secrets.Object)));
            var owner = new Window();
            owner.Show();
            window.Show(owner);
            return (window, vm, owner);
        }
    }

    [AvaloniaFact]
    public void NewConnection_Should_ListEveryDriver_AndManageShouldBeAButton()
    {
        var fixture = new Fixture();
        var (window, _, owner) = fixture.CreateWindow();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var menu = (MenuFlyout)window.FindControl<Button>("NewConnectionButton")!.Flyout!;
            Assert.Equal(["HTTP"], menu.Items.Cast<MenuItem>().Select(i => i.Header));
            Assert.True(window.FindControl<Button>("NewConnectionButton")!.IsEnabled);
            Assert.True(window.FindControl<Button>("ManageConnectionsButton")!.IsEnabled);
        }
        finally { window.Close(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingDependenciesWindow_Should_InvalidatePendingAddConfirmation()
    {
        var fixture = new Fixture();
        fixture.Drivers.Register(new PackageDriver());
        fixture.Mine.Add(new MdRunConnection { Name = "local", Driver = "package" });
        var (window, vm, owner) = fixture.CreateWindow();
        var completion = new TaskCompletionSource<bool>();
        vm.Connections.AddConfirmationDialog = _ => completion.Task;
        try
        {
            vm.Connections.SelectedAvailable = Assert.Single(vm.Connections.Available);
            vm.Connections.AddCommand.Execute().Subscribe();
            window.Close();
            completion.SetResult(true);
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.Connections.Items);
        }
        finally { window.Close(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task NewConnection_Should_OpenTheConnectionsWindowOnANewConnection_AndOfferTheSavedOne()
    {
        var fixture = new Fixture();
        var (window, vm, owner) = fixture.CreateWindow();
        try
        {
            var http = Assert.Single(vm.Connections.Drivers);
            var run = vm.Connections.NewCommand.Execute(http).ToTask();
            Dispatcher.UIThread.RunJobs();

            var editor = fixture.Opened!;
            Assert.True(editor.IsVisible);
            var editorVm = (ConnectionsWindowViewModel)editor.DataContext!;
            Assert.Equal("http", editorVm.SelectedConnection!.DriverId);
            Assert.True(editorVm.HasChanges);

            editor.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["http"], fixture.Mine.Select(c => c.Name));
            // HTTP brings no package, so the saved connection goes into the draft; the document itself
            // is only written by Save in this window.
            Assert.Equal(["http"], vm.Connections.Items.Select(i => i.Name));
            Assert.True(vm.HasChanges);
        }
        finally { window.Close(); owner.Close(); }
    }

    [AvaloniaFact]
    public async Task CancellingTheConnectionsWindow_Should_LeaveTheListAndTheDraftAsTheyWere()
    {
        var fixture = new Fixture();
        fixture.Mine.Add(new MdRunConnection { Name = "kept", Driver = "http" });
        var (window, vm, owner) = fixture.CreateWindow();
        try
        {
            var run = vm.Connections.NewCommand.Execute(Assert.Single(vm.Connections.Drivers)).ToTask();
            Dispatcher.UIThread.RunJobs();
            var editor = fixture.Opened!;
            var editorVm = (ConnectionsWindowViewModel)editor.DataContext!;
            Assert.Equal(2, editorVm.Connections.Count);

            // A programmatic close drops the edits, exactly as Cancel does after the user declines to save.
            editor.Close(false);
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["kept"], fixture.Mine.Select(c => c.Name));
            Assert.Empty(vm.Connections.Items);
            Assert.False(vm.HasChanges);
            fixture.Store.Verify(s => s.Save(It.IsAny<IReadOnlyList<MdRunConnection>>(), It.IsAny<string?>()), Times.Never);
        }
        finally { window.Close(); owner.Close(); }
    }
}
