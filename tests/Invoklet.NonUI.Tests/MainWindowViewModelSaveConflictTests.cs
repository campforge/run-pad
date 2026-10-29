using System.Text;
using Invoklet.Contracts;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.UserControls.Models;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Closing, quitting, Save As and saving before a rename/move/delete go through the same check as
// Ctrl+S: a file changed by another program since the tab last read or wrote it is replaced only on
// the user's Overwrite, Save As… writes elsewhere, and Cancel (or a cancelled picker) stops whatever
// needed the save. The tab's writer is a fake that writes text and records it as the tab's own
// write, like the editor does; its baseline is set as if the tab had read the file.
public class MainWindowViewModelSaveConflictTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("Invoklet-save-conflict").FullName;
    private readonly string _file;
    private readonly MainWindowViewModel _vm;
    private readonly Mock<IWindowService> _windows = new();

    public MainWindowViewModelSaveConflictTests()
    {
        _file = Path.Combine(_root, "a.rpad");
        File.WriteAllText(_file, "read by the tab");

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(_root);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        _vm = new MainWindowViewModel([], fileSystem.Object, _windows.Object, prefs.Object, new LanguageHostRegistry());
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private EditorTabItemViewModel OpenDirtyTab(string text = "edited in Invoklet", Func<string, bool>? fails = null)
    {
        _vm.OpenTab(_file);
        var tab = _vm.Tabs.Single(t => t.FilePath == _file);
        tab.DiskState = ScriptFileState.Of(File.ReadAllBytes(_file));
        tab.WriteToFile = path =>
        {
            if (fails?.Invoke(path) == true) throw new IOException("disk full");
            var bytes = Encoding.UTF8.GetBytes(text);
            File.WriteAllBytes(path, bytes);
            tab.LastWrite = (path, ScriptFileState.Of(bytes));
        };
        tab.IsDirty = true;
        return tab;
    }

    private void ChangeExternally() => File.WriteAllText(_file, "written by another program");

    private void Answer(SaveConflictChoice choice) =>
        _windows.Setup(w => w.ShowSaveConflictDialogAsync("a.rpad")).ReturnsAsync(choice);

    private Task<bool> CloseAsync(EditorTabItemViewModel tab) => _vm.ResolveUnsavedChangesBeforeCloseAsync(tab);

    // ── Close and quit (both resolve each tab here) ──────────────────────────

    [Fact]
    public async Task Close_WithoutAnExternalChange_Should_SaveAndClose_WithoutAsking()
    {
        var tab = OpenDirtyTab();

        Assert.True(await CloseAsync(tab));

        Assert.Equal("edited in Invoklet", File.ReadAllText(_file));
        _windows.Verify(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Close_Cancel_Should_StopTheClose_AndKeepBothVersions()
    {
        var tab = OpenDirtyTab();
        ChangeExternally();
        Answer(SaveConflictChoice.Cancel);

        Assert.False(await CloseAsync(tab));

        Assert.Equal("written by another program", File.ReadAllText(_file));
        Assert.True(tab.IsDirty);
        Assert.Contains(tab, _vm.Tabs);
        _windows.Verify(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Close_Overwrite_Should_WriteTheTab_AndClose()
    {
        var tab = OpenDirtyTab();
        ChangeExternally();
        Answer(SaveConflictChoice.Overwrite);

        Assert.True(await CloseAsync(tab));

        Assert.Equal("edited in Invoklet", File.ReadAllText(_file));
        Assert.False(tab.IsDirty);
    }

    // A failed overwrite is reported, then the existing Save As / Don't Save / Cancel question decides.
    [Fact]
    public async Task Close_AFailedOverwrite_Should_ReportIt_AndFallBackToTheUnsavedChangesQuestion()
    {
        var tab = OpenDirtyTab(fails: _ => true);
        ChangeExternally();
        Answer(SaveConflictChoice.Overwrite);
        _windows.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>())).ReturnsAsync(UnsavedChangesResult.Cancel);

        Assert.False(await CloseAsync(tab));

        _windows.Verify(w => w.ShowErrorAsync("Save Failed", It.Is<string>(m => m.Contains("disk full"))), Times.Once);
        Assert.Equal("written by another program", File.ReadAllText(_file));
        Assert.True(tab.IsDirty);
    }

    // ── Save As… from the question ──────────────────────────────────────────

    [Fact]
    public async Task Close_SaveAs_WithACancelledPicker_Should_StopTheClose_AndChangeNothing()
    {
        var tab = OpenDirtyTab();
        ChangeExternally();
        Answer(SaveConflictChoice.SaveAs);
        _windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((string?)null);

        Assert.False(await CloseAsync(tab));

        Assert.Equal("written by another program", File.ReadAllText(_file));
        Assert.True(tab.IsDirty);
        Assert.Equal(_file, tab.FilePath);
    }

    [Fact]
    public async Task Close_SaveAs_ToAnotherFile_Should_KeepTheChangedFile_AndSaveTheTabThere()
    {
        var tab = OpenDirtyTab();
        ChangeExternally();
        Answer(SaveConflictChoice.SaveAs);
        var other = Path.Combine(_root, "b.rpad");
        _windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(other);

        Assert.True(await CloseAsync(tab));

        Assert.Equal("written by another program", File.ReadAllText(_file));
        Assert.Equal("edited in Invoklet", File.ReadAllText(other));
        Assert.Equal(other, tab.FilePath);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public async Task Close_SaveAs_WithAFailedWrite_Should_ReportIt_AndStopTheClose()
    {
        var other = Path.Combine(_root, "b.rpad");
        var tab = OpenDirtyTab(fails: p => p == other);
        ChangeExternally();
        Answer(SaveConflictChoice.SaveAs);
        _windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(other);

        Assert.False(await CloseAsync(tab));

        _windows.Verify(w => w.ShowErrorAsync("Save Failed", It.IsAny<string>()), Times.Once);
        Assert.Equal("written by another program", File.ReadAllText(_file));
        Assert.True(tab.IsDirty);
        Assert.Equal(_file, tab.FilePath);
    }

    // Choosing the tab's own file in Save As is not a way around the question.
    [Fact]
    public async Task SaveAs_ToTheTabsOwnChangedFile_Should_Ask_AndCancelKeepsIt()
    {
        var tab = OpenDirtyTab();
        ChangeExternally();
        Answer(SaveConflictChoice.Cancel);
        _windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(_file);

        await ((IEditorTabActions)_vm).SaveAsAsync(tab);

        _windows.Verify(w => w.ShowSaveConflictDialogAsync("a.rpad"), Times.Once);
        Assert.Equal("written by another program", File.ReadAllText(_file));
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task SaveAs_ToTheTabsOwnUnchangedFile_Should_Save_WithoutAsking()
    {
        var tab = OpenDirtyTab();
        _windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(_file);

        await ((IEditorTabActions)_vm).SaveAsAsync(tab);

        _windows.Verify(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>()), Times.Never);
        Assert.Equal("edited in Invoklet", File.ReadAllText(_file));
        Assert.False(tab.IsDirty);
    }

    // After Save As to another file, that file is the tab's baseline: its next save doesn't ask.
    [Fact]
    public async Task SaveAs_Should_MakeTheNewFileTheBaseline()
    {
        var tab = OpenDirtyTab();
        var other = Path.Combine(_root, "b.rpad");
        _windows.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(other);
        await ((IEditorTabActions)_vm).SaveAsAsync(tab);
        tab.IsDirty = true;

        Assert.True(await CloseAsync(tab));

        _windows.Verify(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>()), Times.Never);
    }

    // ── Rename, move and delete save first; a declined save stops them ───────

    [Fact]
    public async Task Rename_WithACancelledConflict_Should_LeaveTheFileAndTheTab()
    {
        var tab = OpenDirtyTab();
        ChangeExternally();
        Answer(SaveConflictChoice.Cancel);

        await _vm.RenameFile(_file, "renamed");

        Assert.Equal("written by another program", File.ReadAllText(_file));
        Assert.False(File.Exists(Path.Combine(_root, "renamed.rpad")));
        Assert.Contains(tab, _vm.Tabs);
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task Delete_WithACancelledConflict_Should_NotDelete()
    {
        var tab = OpenDirtyTab();
        ChangeExternally();
        Answer(SaveConflictChoice.Cancel);

        await _vm.DeleteFile(_file);

        Assert.Equal("written by another program", File.ReadAllText(_file));
        Assert.Contains(tab, _vm.Tabs);
    }

    [Fact]
    public async Task Move_WithAnOverwrite_Should_SaveThenMove()
    {
        var tab = OpenDirtyTab();
        ChangeExternally();
        Answer(SaveConflictChoice.Overwrite);
        var target = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;

        await _vm.MoveFileOrFolder(_file, target);

        Assert.False(File.Exists(_file));
        Assert.Equal("edited in Invoklet", File.ReadAllText(Path.Combine(target, "a.rpad")));
    }
}
