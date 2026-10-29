using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Invoklet.Contracts;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// F2 (rename) and Delete used to be literal Key.F2/Key.Delete checks in
// FileTreeView.FileTree_KeyDown — this control has no DI (instantiated by a plain XAML DataTemplate,
// not resolved through the service container), so the gestures are routed through its DataContext
// (FileTreeViewModel.RenameGesture/DeleteGesture) instead. These prove the conversion actually reads
// from KeyboardSettings by rebinding each gesture and confirming the OLD literal key stops working
// while the NEW bound one does — same proof shape as MainWindowHotkeysTests for F4/Ctrl+W.
public class FileTreeViewHotkeysTests
{
    // The Windows/Linux defaults on every OS, so the keys pressed below are the bound ones.
    private static KeyboardSettings Keys(KeyCommand command, string key)
        => new() { IsMac = false, Bindings = { [command.Id] = key } };

    private static (Window Window, FileTreeView View, FileTreeViewNode Node) CreateViewWithOneSelectedNode(
        KeyboardSettings keyboard)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(keyboard);

        var vm = new FileTreeViewModel(prefs.Object, new LanguageHostRegistry());
        vm.Refresh([new FileSystemItem("a.inv", false, "/root/a.inv", null)]);
        var node = vm.Nodes[0];

        var view = new FileTreeView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        var treeView = view.FindControl<TreeView>("FileTree")!;
        treeView.SelectedItem = node;
        // TreeView itself isn't Focusable (same situation as CodeTextEditor not being Focusable while

        // TreeViewItem container for the selected node is what actually needs focus for KeyDown
        // (bound on the TreeView, bubbling up from wherever real keyboard focus sits) to fire at all.
        ((TreeViewItem)treeView.ContainerFromItem(node)!).Focus();

        return (window, view, node);
    }

    [AvaloniaFact]
    public void PressingTheDefaultF2_Should_StartRenaming()
    {
        var (window, _, node) = CreateViewWithOneSelectedNode(new KeyboardSettings { IsMac = false });

        window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);

        Assert.True(node.IsRenaming);
    }

    [AvaloniaFact]
    public void PressingF2_Should_NotStartRenaming_WhenTheGestureHasBeenRebound()
    {
        var (window, _, node) = CreateViewWithOneSelectedNode(Keys(KeyCommands.RenameFile, "Ctrl+Shift+R"));

        window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);

        Assert.False(node.IsRenaming);
    }

    [AvaloniaFact]
    public void PressingTheReboundGesture_Should_StartRenaming()
    {
        var (window, _, node) = CreateViewWithOneSelectedNode(Keys(KeyCommands.RenameFile, "Ctrl+Shift+R"));

        window.KeyPress(Key.R, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.R, null);

        Assert.True(node.IsRenaming);
    }

    [AvaloniaFact]
    public void PressingTheDefaultDeleteKey_Should_RaiseTheDeleteEvent()
    {
        var (window, view, node) = CreateViewWithOneSelectedNode(new KeyboardSettings { IsMac = false });
        string? deletedPath = null;
        view.Delete += (_, e) => deletedPath = e.FilePath;

        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);

        Assert.Equal(node.FilePath, deletedPath);
    }

    [AvaloniaFact]
    public void PressingDeleteKey_Should_NotRaiseTheDeleteEvent_WhenTheGestureHasBeenRebound()
    {
        var (window, view, _) = CreateViewWithOneSelectedNode(Keys(KeyCommands.DeleteFile, "Ctrl+Shift+D"));
        var raised = false;
        view.Delete += (_, _) => raised = true;

        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);

        Assert.False(raised);
    }

    [AvaloniaFact]
    public void PressingTheReboundGesture_Should_RaiseTheDeleteEvent()
    {
        var (window, view, node) = CreateViewWithOneSelectedNode(Keys(KeyCommands.DeleteFile, "Ctrl+Shift+D"));
        string? deletedPath = null;
        view.Delete += (_, e) => deletedPath = e.FilePath;

        window.KeyPress(Key.D, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.D, null);

        Assert.Equal(node.FilePath, deletedPath);
    }
}
