using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        store.Setup(s => s.LoadSnapshot()).Returns(new SecretStoreSnapshot(secrets, "loaded"));
        store.Setup(s => s.FilePath).Returns("/data/RunPad/secrets.json");
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>())).Returns("saved");
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
        store.Verify(s => s.Save(It.Is<IReadOnlyList<Secret>>(list => list.SequenceEqual(new[] { new Secret("db", "new") })), "loaded"));
        owner.Close();
    }
    [AvaloniaFact]
    public async Task SaveFailure_Should_KeepTheDialogAndEdits_ForRetry()
    {
        var (window, store, result, owner) = Open(new Secret("db", "old"));
        try
        {
            var value = window.FindControl<TextBox>("ValueBox")!;
            value.Text = "edited-secret";
            store.Setup(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>())).Throws(new IOException("edited-secret"));
            window.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(result.IsCompleted);
            Assert.Equal("edited-secret", value.Text);
            var error = window.FindControl<TextBlock>("StoreError")!;
            Assert.Contains("Could not save", error.Text);
            Assert.DoesNotContain("edited-secret", error.Text);
            Assert.True(((SecretStoreWindowViewModel)window.DataContext!).CanSave);
            store.Setup(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>())).Returns("saved");
            window.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(await result);
        }
        finally { window.Close(); owner.Close(); }
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
        var (window, store, result, owner) = Open(new Secret("db", "old"));
        try
        {
            RequestClose(window, via);
            Assert.Empty(window.OwnedWindows);
            Assert.False(await result);
            store.Verify(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>()), Times.Never);
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
        var (window, store, result, owner) = Open(new Secret("db", "old"));
        try
        {
            window.FindControl<TextBox>("ValueBox")!.Text = "new";
            RequestClose(window, via);
            Assert.False(result.IsCompleted);
            Choose(window, answer);
            Assert.Equal(saved, await result);
            store.Verify(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>()), Times.Exactly(writes));
        }
        finally { owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(CloseVia.CancelButton)]
    [InlineData(CloseVia.NativeClose)]
    public async Task CancellingTheQuestion_Should_KeepTheWindowAndDraft_AndAskOnlyOnce(CloseVia via)
    {
        var (window, store, result, owner) = Open(new Secret("db", "old"));
        try
        {
            window.FindControl<TextBox>("ValueBox")!.Text = "new";
            RequestClose(window, via);
            RequestClose(window, via); // while the question is up: no second one
            Assert.Single(window.OwnedWindows);
            Choose(window, "Cancel");
            Assert.False(result.IsCompleted);
            Assert.Empty(window.OwnedWindows);
            Assert.True(((SecretStoreWindowViewModel)window.DataContext!).HasChanges);
            store.Verify(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>()), Times.Never);

            RequestClose(window, via); // still asks the next time
            Choose(window, "Don’t Save");
            Assert.False(await result);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task SaveFromTheQuestion_Should_KeepTheWindowOpen_WhenSavingFails()
    {
        var (window, store, result, owner) = Open(new Secret("db", "old"));
        try
        {
            window.FindControl<TextBox>("ValueBox")!.Text = "new";
            store.Setup(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>())).Throws(new IOException("private text"));
            RequestClose(window, CloseVia.CancelButton);
            Choose(window, "Save");
            Assert.False(result.IsCompleted);
            Assert.True(((SecretStoreWindowViewModel)window.DataContext!).HasChanges);

            store.Setup(s => s.Save(It.IsAny<IReadOnlyList<Secret>>(), It.IsAny<string?>())).Returns("saved");
            RequestClose(window, CloseVia.CancelButton);
            Choose(window, "Save");
            Assert.True(await result);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task OwnerShutdown_Should_NotAsk()
    {
        var (window, _, result, owner) = Open(new Secret("db", "old"));
        window.FindControl<TextBox>("ValueBox")!.Text = "new";
        owner.Close();
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(window.OwnedWindows);
        Assert.False(await result);
    }
}
