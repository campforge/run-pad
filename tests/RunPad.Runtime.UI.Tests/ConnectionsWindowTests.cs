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
        store.Setup(s => s.Load()).Returns(
        [
            new RpadConnection
            {
                Name = "billing",
                Driver = "http",
                Params = new(StringComparer.Ordinal) { ["baseAddress"] = RpadValue.Of("https://billing.local/") },
            },
        ]);
        store.Setup(s => s.FilePath).Returns("/data/RunPad/connections.json");
        IReadOnlyList<RpadConnection>? saved = null;
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>())).Callback<IReadOnlyList<RpadConnection>>(list => saved = list);
        var secrets = new Mock<ISecretStore>();
        secrets.Setup(s => s.Load()).Returns([new Secret("billing-token", "t0k")]);
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

        window.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(await result);
        Assert.Equal("https://billing.example/", Assert.Single(saved!).Params["baseAddress"].Text);
        owner.Close();
    }
}
