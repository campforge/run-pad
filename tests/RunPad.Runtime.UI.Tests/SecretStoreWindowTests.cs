using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Moq;
using RunPad.Services.Secrets;
using RunPad.Windows;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public class SecretStoreWindowTests
{
    private static (SecretStoreWindow Window, Mock<ISecretStore> Store, Task<bool> Result, Window Owner) Open(params Secret[] secrets)
    {
        var store = new Mock<ISecretStore>();
        store.Setup(s => s.Load()).Returns(secrets);
        store.Setup(s => s.FilePath).Returns("/data/RunPad/secrets.json");
        var owner = new Window();
        owner.Show();
        var window = new SecretStoreWindow(new SecretStoreWindowViewModel(store.Object));
        var result = window.ShowDialog<bool>(owner);
        Dispatcher.UIThread.RunJobs();
        return (window, store, result, owner);
    }

    // The list shows names only; the value is dots until Show is ticked.
    [AvaloniaFact]
    public async Task TheValue_Should_StayHidden_UntilShown()
    {
        var (window, _, result, owner) = Open(new Secret("db", "p@ss"));
        var value = window.FindControl<TextBox>("ValueBox")!;
        Assert.Equal("p@ss", value.Text);
        Assert.Equal('●', value.PasswordChar);

        window.FindControl<CheckBox>("ShowValueBox")!.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal('\0', value.PasswordChar);

        window.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(await result);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Ok_Should_SaveTheEditedStore_AndCancelNothing()
    {
        var (window, store, result, owner) = Open(new Secret("db", "old"));
        window.FindControl<TextBox>("ValueBox")!.Text = "new";
        window.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(await result);
        store.Verify(s => s.Save(It.Is<IReadOnlyList<Secret>>(list => list.SequenceEqual(new[] { new Secret("db", "new") }))));
        owner.Close();
    }
}
