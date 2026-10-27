using Moq;
using RunPad.Services.Preferences;
using RunPad.UserControls.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// CloseCommand/SaveAsCommand/DuplicateCommand/OpenContainingFolderCommand used to
// be constructed with empty bodies (ReactiveCommand.Create(() => { })) and had their real behavior
// subscribed onto them from EditorTabsControl — a class that's neither this tab's View nor its
// ViewModel. Now each command's body directly calls the injected IEditorTabActions, so invoking the
// command IS the behavior, provable here with nothing but a mock and no Avalonia involved at all.
public class EditorTabItemViewModelActionsTests
{
    private static (EditorTabItemViewModel Vm, Mock<IEditorTabActions> Actions) CreateVm()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());

        var actions = new Mock<IEditorTabActions>();
        var vm = new EditorTabItemViewModel(null, [], prefs.Object, actions.Object);
        return (vm, actions);
    }

    [Fact]
    public void CloseCommand_Should_InvokeActionsCloseAsync_WithThisTab()
    {
        var (vm, actions) = CreateVm();
        actions.Setup(a => a.CloseAsync(vm)).Returns(Task.CompletedTask);

        vm.CloseCommand.Execute().Subscribe();

        actions.Verify(a => a.CloseAsync(vm), Times.Once);
    }

    [Fact]
    public void SaveAsCommand_Should_InvokeActionsSaveAsAsync_WithThisTab()
    {
        var (vm, actions) = CreateVm();
        actions.Setup(a => a.SaveAsAsync(vm)).Returns(Task.CompletedTask);

        vm.SaveAsCommand.Execute().Subscribe();

        actions.Verify(a => a.SaveAsAsync(vm), Times.Once);
    }

    [Fact]
    public void DuplicateCommand_Should_InvokeActionsDuplicate_WithThisTab()
    {
        var (vm, actions) = CreateVm();

        vm.DuplicateCommand.Execute().Subscribe();

        actions.Verify(a => a.Duplicate(vm), Times.Once);
    }

    [Fact]
    public void OpenContainingFolderCommand_Should_InvokeActionsOpenContainingFolder_WithThisTab()
    {
        var (vm, actions) = CreateVm();

        vm.OpenContainingFolderCommand.Execute().Subscribe();

        actions.Verify(a => a.OpenContainingFolder(vm), Times.Once);
    }
}
