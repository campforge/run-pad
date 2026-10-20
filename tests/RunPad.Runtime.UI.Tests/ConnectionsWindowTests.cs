using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using RunPad.Connectors.Http;
using RunPad.Contracts.Connections;
using RunPad.Format.RPad;
using RunPad.Services.Connections;
using RunPad.Services.Secrets;
using RunPad.Windows;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public class ConnectionsWindowTests
{
    [AvaloniaFact]
    public async Task TheForm_Should_FollowTheDriver_AndOkShouldSaveTheEdits()
    {
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.LoadSnapshot()).Returns(new ConnectionStoreSnapshot(
        [
            new RpadConnection
            {
                Name = "billing",
                Driver = "http",
                Params = new(StringComparer.Ordinal)
                {
                    ["baseAddress"] = RpadValue.Of("https://billing.local/"),
                    ["headers"] = RpadValue.Map([new("Accept", RpadValue.Of("application/json"))]),
                },
            },
        ], null));
        store.Setup(s => s.FilePath).Returns("/data/RunPad/connections.json");
        IReadOnlyList<RpadConnection>? saved = null;
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>())).Callback<IReadOnlyList<RpadConnection>, string?>((list, _) => saved = list);
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

        // A row's remove button names the row in its tooltip, as in its accessible name.
        var removeRow = fields.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "✕"));
        var row = (ConnectionMapRowViewModel)removeRow.DataContext!;
        Assert.Equal(row.RemoveRowLabel, ToolTip.GetTip(removeRow));
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
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>()))
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
            store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>())).Returns("saved");
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(await result);
        }
        finally
        {
            window.Close();
            owner.Close();
        }
    }
}
