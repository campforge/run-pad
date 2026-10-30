using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Invoklet.Contracts;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.UserControls;

// File tree had no drag-and-drop at all. These test FileTreeView's own DragOver/Drop
// routing directly — constructing a real DragEventArgs (it has a public constructor) and raising it on
// a REAL, realized TreeViewItem container (not a floating, unattached one — RaiseEvent overwrites
// Source to whatever element it's called on, so the event has to originate from the actual container
// for Source to end up being it once the event bubbles to FileTreeView's own handlers, same as a real
// drop would). See MainWindowViewModelMoveTests.cs for the actual move-on-disk logic
// (MoveFileOrFolder) this ultimately triggers, and FileTreeViewHotkeysTests.cs for the same
// real-window-plus-container precedent this borrows.
public class FileTreeViewDragDropTests
{
    private static DataTransfer BuildNodeDragData(string filePath)
    {
        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(FileTreeView.NodePathFormat, filePath);
        data.Add(item);
        return data;
    }

    private static (FileTreeView View, TreeView TreeView, FileTreeViewNode Folder, FileTreeViewNode File) CreateViewWithRealizedNodes()
    {
        var prefs = new Mock<IPreferencesService>();
        var vm = new FileTreeViewModel(prefs.Object, new LanguageHostRegistry());
        vm.Refresh([
            new FileSystemItem("Sub", true, "/root/Sub", []),
            new FileSystemItem("a.inv", false, "/root/a.inv", null),
        ]);

        var view = new FileTreeView { DataContext = vm };
        var window = new Window { Content = view };
        // The real app loads this from Invoklet.Avalonia.Themes; TestAppBuilder deliberately doesn't
        // (see its own doc comment) — registered directly here so the empty-tree-space highlight
        // (FileTreeView.UpdateDropHighlight's TryFindResource lookup) has something to resolve.
        window.Resources["Invoklet.Accent.BackgroundBrush"] = Brushes.CornflowerBlue;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var treeView = view.FindControl<TreeView>("FileTree")!;
        var folder = vm.Nodes.Single(n => n.FilePath == "/root/Sub");
        var file = vm.Nodes.Single(n => n.FilePath == "/root/a.inv");
        return (view, treeView, folder, file);
    }

    [AvaloniaFact]
    public void DragOver_Should_AllowMove_WhenTheDragCarriesANodePath()
    {
        var (_, treeView, folder, _) = CreateViewWithRealizedNodes();
        var container = (TreeViewItem)treeView.ContainerFromItem(folder)!;
        var data = BuildNodeDragData("/root/a.inv");
        var e = new DragEventArgs(DragDrop.DragOverEvent, data, container, default, KeyModifiers.None);

        container.RaiseEvent(e);

        Assert.Equal(DragDropEffects.Move, e.DragEffects);
        Assert.True(e.Handled);
    }

    [AvaloniaFact]
    public void DragOver_Should_NotAllowDrop_WhenTheDragIsNotFromThisTree()
    {
        var (_, treeView, folder, _) = CreateViewWithRealizedNodes();
        var container = (TreeViewItem)treeView.ContainerFromItem(folder)!;
        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, "just some text, not one of our nodes");
        data.Add(item);
        var e = new DragEventArgs(DragDrop.DragOverEvent, data, container, default, KeyModifiers.None);

        container.RaiseEvent(e);

        Assert.Equal(DragDropEffects.None, e.DragEffects);
    }

    [AvaloniaFact]
    public void Drop_Should_RaiseMove_WithTheFolderNodesPath_WhenDroppedOnAFolder()
    {
        var (view, treeView, folder, _) = CreateViewWithRealizedNodes();
        var container = (TreeViewItem)treeView.ContainerFromItem(folder)!;
        FileTreeMoveEventArgs? raised = null;
        view.Move += (_, e) => raised = e;

        var data = BuildNodeDragData("/root/a.inv");
        var dropArgs = new DragEventArgs(DragDrop.DropEvent, data, container, default, KeyModifiers.None);
        container.RaiseEvent(dropArgs);

        Assert.NotNull(raised);
        Assert.Equal("/root/a.inv", raised!.SourcePath);
        Assert.Equal("/root/Sub", raised.TargetPath);
    }

    [AvaloniaFact]
    public void Drop_Should_RaiseMove_WithTheFileNodesPath_WhenDroppedOnAFile()
    {
        var (view, treeView, _, file) = CreateViewWithRealizedNodes();
        var container = (TreeViewItem)treeView.ContainerFromItem(file)!;
        FileTreeMoveEventArgs? raised = null;
        view.Move += (_, e) => raised = e;

        var data = BuildNodeDragData("/root/Sub/b.inv");
        var dropArgs = new DragEventArgs(DragDrop.DropEvent, data, container, default, KeyModifiers.None);
        container.RaiseEvent(dropArgs);

        Assert.NotNull(raised);
        Assert.Equal("/root/a.inv", raised!.TargetPath);
    }

    // No FileTreeViewNode under the pointer at all (dropped on empty tree space) — TargetPath null
    // means "the workspace root" (MainWindowViewModel.ResolveParentDir).
    [AvaloniaFact]
    public void Drop_Should_RaiseMove_WithNullTargetPath_WhenDroppedOnEmptyTreeSpace()
    {
        var (view, treeView, _, _) = CreateViewWithRealizedNodes();
        FileTreeMoveEventArgs? raised = null;
        view.Move += (_, e) => raised = e;

        var data = BuildNodeDragData("/root/a.inv");
        var dropArgs = new DragEventArgs(DragDrop.DropEvent, data, treeView, default, KeyModifiers.None);
        treeView.RaiseEvent(dropArgs);

        Assert.NotNull(raised);
        Assert.Null(raised!.TargetPath);
    }

    [AvaloniaFact]
    public void Drop_Should_NotRaiseMove_WhenTheDragIsNotFromThisTree()
    {
        var (view, treeView, folder, _) = CreateViewWithRealizedNodes();
        var container = (TreeViewItem)treeView.ContainerFromItem(folder)!;
        var raised = false;
        view.Move += (_, _) => raised = true;

        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, "just some text, not one of our nodes");
        data.Add(item);
        var dropArgs = new DragEventArgs(DragDrop.DropEvent, data, container, default, KeyModifiers.None);
        container.RaiseEvent(dropArgs);

        Assert.False(raised);
    }

    // Live user report: the OS drag cursor alone wasn't a reliable/visible enough
    // signal of where a drop would land — the hovered node highlights directly too.
    [AvaloniaFact]
    public void DragOver_Should_HighlightTheHoveredNode()
    {
        var (_, treeView, folder, _) = CreateViewWithRealizedNodes();
        var container = (TreeViewItem)treeView.ContainerFromItem(folder)!;
        var data = BuildNodeDragData("/root/a.inv");

        container.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, data, container, default, KeyModifiers.None));

        Assert.True(folder.IsDropTarget);
    }

    [AvaloniaFact]
    public void DragOver_Should_MoveTheHighlight_OffThePreviousNode_WhenHoveringADifferentOne()
    {
        var (_, treeView, folder, file) = CreateViewWithRealizedNodes();
        var folderContainer = (TreeViewItem)treeView.ContainerFromItem(folder)!;
        var fileContainer = (TreeViewItem)treeView.ContainerFromItem(file)!;
        var data = BuildNodeDragData("/root/Sub/b.inv");

        folderContainer.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, data, folderContainer, default, KeyModifiers.None));
        Assert.True(folder.IsDropTarget);

        fileContainer.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, data, fileContainer, default, KeyModifiers.None));

        Assert.False(folder.IsDropTarget);
        Assert.True(file.IsDropTarget);
    }

    [AvaloniaFact]
    public void DragLeave_Should_ClearTheHighlight()
    {
        var (_, treeView, folder, _) = CreateViewWithRealizedNodes();
        var container = (TreeViewItem)treeView.ContainerFromItem(folder)!;
        var data = BuildNodeDragData("/root/a.inv");
        container.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, data, container, default, KeyModifiers.None));
        Assert.True(folder.IsDropTarget);

        container.RaiseEvent(new DragEventArgs(DragDrop.DragLeaveEvent, data, container, default, KeyModifiers.None));

        Assert.False(folder.IsDropTarget);
    }

    [AvaloniaFact]
    public void Drop_Should_ClearTheHighlight()
    {
        var (_, treeView, folder, _) = CreateViewWithRealizedNodes();
        var container = (TreeViewItem)treeView.ContainerFromItem(folder)!;
        var data = BuildNodeDragData("/root/a.inv");
        container.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, data, container, default, KeyModifiers.None));
        Assert.True(folder.IsDropTarget);

        container.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, container, default, KeyModifiers.None));

        Assert.False(folder.IsDropTarget);
    }

    // Dropping onto empty tree space still highlights something — the tree's own background,
    // since there's no specific node to highlight (targets the workspace root).
    [AvaloniaFact]
    public void DragOver_Should_HighlightTheTreeBackground_WhenHoveringEmptySpace()
    {
        var (_, treeView, _, _) = CreateViewWithRealizedNodes();
        var data = BuildNodeDragData("/root/a.inv");

        treeView.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, data, treeView, default, KeyModifiers.None));

        Assert.NotEqual(Brushes.Transparent, treeView.Background);
    }

    [AvaloniaFact]
    public void DragLeave_Should_ClearTheTreeBackgroundHighlight()
    {
        var (_, treeView, _, _) = CreateViewWithRealizedNodes();
        var data = BuildNodeDragData("/root/a.inv");
        treeView.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, data, treeView, default, KeyModifiers.None));
        Assert.NotEqual(Brushes.Transparent, treeView.Background);

        treeView.RaiseEvent(new DragEventArgs(DragDrop.DragLeaveEvent, data, treeView, default, KeyModifiers.None));

        Assert.Equal(Brushes.Transparent, treeView.Background);
    }
}
