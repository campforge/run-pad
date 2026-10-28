using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// An open tab doesn't reload its file, and saving used to write the tab's text over the file
// whatever was there: Ctrl+S on a clean tab, or a front-matter edit saving by itself, silently
// replaced what another program had written. Now an in-place save compares the file with the exact
// bytes the tab last read or wrote, and asks — Save As… / Overwrite / Cancel — before replacing a
// changed file. Real tabs on temporary files; the dialog's answer comes from the window service.
public class ExternalFileChangeSaveTests
{
    private const string Original = "Console.WriteLine(\"original\");";
    private const string External = "Console.WriteLine(\"external\");";
    private const string Sameness = "Console.WriteLine(\"origin-l\");"; // same length as Original
    private const string EditedHere = "Console.WriteLine(\"edited in RunPad\");";

    private sealed class Tab(EditorsTabItemControl ctrl, EditorTabItemViewModel vm, Window host, Mock<IWindowService> windows,
        Mock<IEditorTabActions> actions) : IDisposable
    {
        public EditorsTabItemControl Ctrl { get; } = ctrl;
        public EditorTabItemViewModel Vm { get; } = vm;
        public Mock<IWindowService> Windows { get; } = windows;
        public Mock<IEditorTabActions> Actions { get; } = actions;

        public void Answer(SaveConflictChoice choice) =>
            Windows.Setup(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>())).ReturnsAsync(choice);

        public void AssertAsked(int times) =>
            Windows.Verify(w => w.ShowSaveConflictDialogAsync("demo.rpad"), Times.Exactly(times));

        public void Dispose()
        {
            host.Close();
            Ctrl.Dispose();
        }
    }

    private static Tab Open(string path)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        var windows = new Mock<IWindowService>();
        var actions = new Mock<IEditorTabActions>();
        var vm = new EditorTabItemViewModel(path, [], prefs.Object, actions.Object);
        var ctrl = new EditorsTabItemControl(vm, theme.Object, windows.Object, new Mock<ILanguageHostRegistry>().Object,
            prefs.Object, intelliSense: null, new Mock<IBuildCacheService>().Object);
        var host = new Window { Content = ctrl };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        return new Tab(ctrl, vm, host, windows, actions);
    }

    private static string? CodeIn(string path) =>
        File.Exists(path) ? RpadParser.TryParse(File.ReadAllText(path))?.RunCode.Trim() : null;

    private static void InTemporaryScript(Action<string, string> test)
    {
        var dir = Directory.CreateTempSubdirectory("runpad-external-change").FullName;
        try
        {
            var path = Path.Combine(dir, "demo.rpad");
            File.WriteAllText(path, RpadWriter.NewFile(Original + "\n", "Demo"));
            test(dir, path);
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
                foreach (var file in Directory.GetFiles(dir))
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(dir, recursive: true);
        }
    }

    // How the other program changes the file.
    public enum Change { Write, AtomicReplace, DeleteAndRecreate, SameLengthKeepingTheTime }

    private static void ChangeExternally(string path, Change change, string code = External)
    {
        var text = RpadWriter.NewFile(code + "\n", "Demo");
        switch (change)
        {
            case Change.Write:
                File.WriteAllText(path, text);
                break;
            case Change.AtomicReplace:
                File.WriteAllText(path + ".tmp", text);
                File.Move(path + ".tmp", path, overwrite: true);
                break;
            case Change.DeleteAndRecreate:
                File.Delete(path);
                File.WriteAllText(path, text);
                break;
            case Change.SameLengthKeepingTheTime:
                var time = File.GetLastWriteTimeUtc(path);
                File.WriteAllText(path, RpadWriter.NewFile(Sameness + "\n", "Demo"));
                File.SetLastWriteTimeUtc(path, time);
                break;
        }
    }

    // ── Cancel: the file on disk stays, the tab keeps its version as unsaved ──

    [AvaloniaTheory]
    [InlineData(Change.Write)]
    [InlineData(Change.AtomicReplace)]
    [InlineData(Change.DeleteAndRecreate)]
    [InlineData(Change.SameLengthKeepingTheTime)]
    public void SaveOnACleanTab_AfterAnExternalChange_Should_Ask_AndCancelKeepsTheFile(Change change)
    {
        InTemporaryScript((_, path) =>
        {
            using var tab = Open(path);
            var before = File.ReadAllBytes(path);
            ChangeExternally(path, change);
            var changed = File.ReadAllBytes(path);
            Assert.NotEqual(before, changed);
            tab.Answer(SaveConflictChoice.Cancel);

            tab.Ctrl.SaveIfNeeded(); // what Ctrl+S does for a tab with a path

            tab.AssertAsked(1);
            Assert.Equal(changed, File.ReadAllBytes(path));
            Assert.True(tab.Vm.IsDirty); // the tab's text is now the unsaved version
            Assert.Equal(Original, tab.Ctrl.State.Editor.Text.Trim());
            Assert.StartsWith("Not saved", tab.Ctrl.State.ExecStatusText, StringComparison.Ordinal);
        });
    }

    [AvaloniaFact]
    public void SaveOnADirtyTab_AfterAnExternalChange_Should_Ask_AndCancelKeepsTheFile()
    {
        InTemporaryScript((_, path) =>
        {
            using var tab = Open(path);
            tab.Ctrl.State.Editor.Text = EditedHere + "\n";
            Dispatcher.UIThread.RunJobs();
            ChangeExternally(path, Change.Write);
            tab.Answer(SaveConflictChoice.Cancel);

            tab.Ctrl.SaveIfNeeded();

            tab.AssertAsked(1);
            Assert.Equal(External, CodeIn(path));
            Assert.True(tab.Vm.IsDirty);
            Assert.Equal(EditedHere, tab.Ctrl.State.Editor.Text.Trim());
        });
    }

    // A front-matter edit saves by itself; it must ask too, and a cancelled save keeps the edit unsaved.
    [AvaloniaFact]
    public void AFrontMatterEdit_AfterAnExternalChange_Should_Ask_AndCancelKeepsTheFile()
    {
        InTemporaryScript((_, path) =>
        {
            using var tab = Open(path);
            ChangeExternally(path, Change.Write);
            tab.Answer(SaveConflictChoice.Cancel);

            tab.Vm.SelectedKind = nameof(RpadKind.Program);
            Dispatcher.UIThread.RunJobs();

            tab.AssertAsked(1);
            Assert.Equal(External, CodeIn(path));
            Assert.Equal(RpadKind.Statements, RpadParser.TryParse(File.ReadAllText(path))!.FrontMatter.Kind);
            Assert.True(tab.Vm.IsDirty);
            Assert.Equal(RpadKind.Program, tab.Ctrl.State.RpadDocument!.FrontMatter.Kind);
        });
    }

    // ── Overwrite: written only on that choice; failure keeps the tab unsaved ──

    [AvaloniaFact]
    public void Overwrite_Should_WriteTheTab_AndBecomeTheNewBaseline()
    {
        InTemporaryScript((_, path) =>
        {
            using var tab = Open(path);
            ChangeExternally(path, Change.Write);
            tab.Answer(SaveConflictChoice.Overwrite);

            tab.Ctrl.SaveIfNeeded();

            Assert.Equal(Original, CodeIn(path));
            Assert.False(tab.Vm.IsDirty);

            // Its own write is not "a change by another program": the next save just writes.
            tab.Ctrl.State.Editor.Text = EditedHere + "\n";
            Dispatcher.UIThread.RunJobs();
            tab.Ctrl.SaveIfNeeded();

            tab.AssertAsked(1);
            Assert.Equal(EditedHere, CodeIn(path));
            Assert.False(tab.Vm.IsDirty);
        });
    }

    [AvaloniaFact]
    public void AFailedOverwrite_Should_KeepTheTabUnsaved_AndReportIt_WithoutRetrying()
    {
        if (OperatingSystem.IsWindows()) return; // read-only through Unix file modes
        InTemporaryScript((_, path) =>
        {
            using var tab = Open(path);
            ChangeExternally(path, Change.Write);
            var external = File.ReadAllBytes(path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead);
            tab.Answer(SaveConflictChoice.Overwrite);

            tab.Ctrl.SaveIfNeeded();

            tab.AssertAsked(1);
            Assert.Equal(external, File.ReadAllBytes(path));
            Assert.True(tab.Vm.IsDirty);
            Assert.StartsWith("Save failed", tab.Ctrl.State.ExecStatusText, StringComparison.Ordinal);
            Assert.Equal("RunPad.Status.ErrorBrush", tab.Ctrl.State.ExecStatusColor);
        });
    }

    // ── Save As…: the tab's Save As flow; its result decides ──

    [AvaloniaFact]
    public void SaveAs_Should_RunTheTabsSaveAs_AndLeaveTheFileAlone_WhenItDoesNotSave()
    {
        InTemporaryScript((_, path) =>
        {
            using var tab = Open(path);
            ChangeExternally(path, Change.Write);
            tab.Answer(SaveConflictChoice.SaveAs);
            tab.Actions.Setup(a => a.SaveAsAsync(tab.Vm)).Returns(Task.CompletedTask); // the picker was cancelled

            tab.Ctrl.SaveIfNeeded();

            tab.Actions.Verify(a => a.SaveAsAsync(tab.Vm), Times.Once);
            Assert.Equal(External, CodeIn(path));
            Assert.True(tab.Vm.IsDirty);
        });
    }

    // ── No change by anyone else: no question ──

    [AvaloniaFact]
    public void SavesWithoutAnExternalChange_Should_NotAsk()
    {
        InTemporaryScript((_, path) =>
        {
            using var tab = Open(path);
            tab.Ctrl.SaveIfNeeded();                                    // clean, unchanged
            tab.Ctrl.State.Editor.Text = EditedHere + "\n";
            Dispatcher.UIThread.RunJobs();
            tab.Ctrl.SaveIfNeeded();                                    // dirty, unchanged since its own save

            tab.AssertAsked(0);
            Assert.Equal(EditedHere, CodeIn(path));
            Assert.False(tab.Vm.IsDirty);
        });
    }

    // A file that is gone can be written again (nothing on disk is lost); a rename leaves the
    // moved file alone.
    [AvaloniaFact]
    public void SaveAfterTheFileWasDeletedOrRenamed_Should_RecreateIt_WithoutAsking()
    {
        InTemporaryScript((dir, path) =>
        {
            using var tab = Open(path);
            var renamed = Path.Combine(dir, "renamed.rpad");
            File.Move(path, renamed);

            tab.Ctrl.SaveIfNeeded();

            tab.AssertAsked(0);
            Assert.Equal(Original, CodeIn(path));
            Assert.Equal(Original, CodeIn(renamed));
        });
    }

    // A tab opened on a missing path didn't read anything: a file created there later is someone else's.
    [AvaloniaFact]
    public void AFileCreatedAfterOpeningAMissingPath_Should_BeAConflict()
    {
        InTemporaryScript((dir, _) =>
        {
            var path = Path.Combine(dir, "later.rpad");
            using var tab = Open(path);
            File.WriteAllText(path, RpadWriter.NewFile(External + "\n", "Demo"));
            tab.Answer(SaveConflictChoice.Cancel);

            tab.Ctrl.SaveIfNeeded();

            tab.Windows.Verify(w => w.ShowSaveConflictDialogAsync("later.rpad"), Times.Once);
            Assert.Equal(External, CodeIn(path));
        });
    }

    // Saves requested while the question is open get its answer instead of another dialog.
    [AvaloniaFact]
    public async Task SavesWhileTheQuestionIsOpen_Should_ShareOneDialog()
    {
        var dir = Directory.CreateTempSubdirectory("runpad-external-change").FullName;
        try
        {
            var path = Path.Combine(dir, "demo.rpad");
            File.WriteAllText(path, RpadWriter.NewFile(Original + "\n", "Demo"));
            using var tab = Open(path);
            ChangeExternally(path, Change.Write);
            var answer = new TaskCompletionSource<SaveConflictChoice>();
            tab.Windows.Setup(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>())).Returns(answer.Task);

            var first = tab.Ctrl.SaveAsync();
            tab.Vm.SelectedKind = nameof(RpadKind.Program); // a front-matter save while waiting
            var second = tab.Ctrl.SaveAsync();
            answer.SetResult(SaveConflictChoice.Overwrite);
            await Task.WhenAll(first, second);

            tab.AssertAsked(1);
            Assert.Equal(RpadKind.Program, RpadParser.TryParse(File.ReadAllText(path))!.FrontMatter.Kind);
            Assert.False(tab.Vm.IsDirty);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
