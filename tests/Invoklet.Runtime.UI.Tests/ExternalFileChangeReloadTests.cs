using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Open tabs follow files another program changed: a tab without unsaved changes reloads quietly,
// one with unsaved changes asks first and isn't asked again about the same version, and File →
// Revert to Saved goes back to the file on demand. Real tabs on temporary files; the dialogs'
// answers come from the window service.
public class ExternalFileChangeReloadTests
{
    private const string Original = "Console.WriteLine(\"original\");";
    private const string External = "Console.WriteLine(\"external\");";
    private const string EditedHere = "Console.WriteLine(\"edited in Invoklet\");";

    private sealed class Harness : IDisposable
    {
        private readonly Window _host;

        public string Folder { get; }
        public string Path { get; }
        public EditorTabsControl Tabs { get; }
        public MainWindowViewModel MainVm { get; }
        public Mock<IWindowService> Windows { get; } = new();

        public Harness()
        {
            Folder = Directory.CreateTempSubdirectory("Invoklet-external-reload").FullName;
            Path = System.IO.Path.Combine(Folder, "demo.inv");
            File.WriteAllText(Path, MdRunWriter.NewFile(Original + "\n", "Demo"));

            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
            prefs.Setup(p => p.Session).Returns(new SessionSettings());
            prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());

            var fileSystem = new Mock<IFileSystemService>();
            fileSystem.Setup(f => f.Folder).Returns(Folder);
            fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var theme = new Mock<IThemeService>();
            theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
                new InterfaceTheme("Test", ThemeVariant.Dark, true),
                new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

            MainVm = new MainWindowViewModel([], fileSystem.Object, Windows.Object, prefs.Object, new LanguageHostRegistry());
            Tabs = new EditorTabsControl(theme.Object, Windows.Object,
                new Mock<ILanguageHostRegistry>().WithCSharpIntelliSense(() => new Mock<IIntelliSenseService>().Object),
                prefs.Object, new Mock<IBuildCacheService>().Object);
            _host = new Window { Content = Tabs };
            _host.Show();
            Tabs.Attach(MainVm);
        }

        public async Task<(EditorTabItemViewModel Vm, EditorsTabItemControl Ctrl)> OpenAsync()
        {
            MainVm.OpenTab(Path);
            var vm = MainVm.Tabs.Single(t => t.FilePath == Path);
            await TestWait.WhileAsync(() => Tabs.ControlForTests(vm) is null);
            return (vm, Tabs.ControlForTests(vm)!);
        }

        public void AnswerReload(bool reload) =>
            Windows.Setup(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>())).ReturnsAsync(reload);

        public void AssertAskedToReload(int times) =>
            Windows.Verify(w => w.ShowReloadChangedFileDialogAsync("demo.inv"), Times.Exactly(times));

        public void Dispose()
        {
            try
            {
                foreach (var tab in MainVm.Tabs) tab.IsDirty = false;
                _host.Close();
            }
            finally
            {
                Tabs.Dispose();
                MainVm.Dispose();
                Directory.Delete(Folder, recursive: true);
            }
        }
    }

    private static void WriteExternally(string path, string code, MdRunKind kind = MdRunKind.Statements,
        MdRunLanguage language = MdRunLanguage.CSharp)
        => File.WriteAllText(path, MdRunWriter.NewFile(code + "\n", "Demo", kind: kind, language: language));

    [AvaloniaFact]
    public async Task ACleanTab_Should_ReloadQuietly_AsOneUndoableEdit()
    {
        using var h = new Harness();
        var (vm, ctrl) = await h.OpenAsync();
        WriteExternally(h.Path, External);
        var onDisk = File.ReadAllBytes(h.Path);

        await h.Tabs.CheckFilesChangedOnDiskAsync();

        Assert.Equal(External, ctrl.State.Editor.Text.Trim());
        Assert.False(vm.IsDirty);
        h.AssertAskedToReload(0);
        Assert.Equal(onDisk, File.ReadAllBytes(h.Path)); // reloading never writes

        ctrl.State.Editor.Undo();
        Assert.Equal(Original, ctrl.State.Editor.Text.Trim());
        Assert.True(vm.IsDirty);
    }

    [AvaloniaFact]
    public async Task AReloadedTab_Should_SaveWithoutAConflict()
    {
        using var h = new Harness();
        var (vm, ctrl) = await h.OpenAsync();
        WriteExternally(h.Path, External);
        await h.Tabs.CheckFilesChangedOnDiskAsync();

        ctrl.State.Editor.Text = EditedHere + "\n";
        ctrl.SaveIfNeeded();
        await TestWait.WhileAsync(() => vm.IsDirty);

        h.Windows.Verify(w => w.ShowSaveConflictDialogAsync(It.IsAny<string>()), Times.Never);
        Assert.Equal(EditedHere, MdRunParser.TryParse(File.ReadAllText(h.Path))!.RunCode.Trim());
    }

    [AvaloniaFact]
    public async Task AnUnchangedFile_Should_LeaveTheTabAlone()
    {
        using var h = new Harness();
        var (vm, ctrl) = await h.OpenAsync();
        ctrl.State.Editor.Text = EditedHere + "\n";

        await h.Tabs.CheckFilesChangedOnDiskAsync();

        h.AssertAskedToReload(0);
        Assert.True(vm.IsDirty);
        Assert.Equal(EditedHere, ctrl.State.Editor.Text.Trim());
    }

    [AvaloniaFact]
    public async Task ADeletedFile_Should_LeaveTheTabAlone()
    {
        using var h = new Harness();
        var (vm, ctrl) = await h.OpenAsync();
        File.Delete(h.Path);

        await h.Tabs.CheckFilesChangedOnDiskAsync();

        Assert.Equal(Original, ctrl.State.Editor.Text.Trim());
        Assert.False(vm.IsDirty);
        Assert.Single(h.MainVm.Tabs);
    }

    [AvaloniaFact]
    public async Task ADirtyTab_Should_AskOnce_AndKeepItsChanges()
    {
        using var h = new Harness();
        var (vm, ctrl) = await h.OpenAsync();
        ctrl.State.Editor.Text = EditedHere + "\n";
        WriteExternally(h.Path, External);
        h.AnswerReload(false);

        await h.Tabs.CheckFilesChangedOnDiskAsync();
        await h.Tabs.CheckFilesChangedOnDiskAsync();

        h.AssertAskedToReload(1);
        Assert.True(vm.IsDirty);
        Assert.Equal(EditedHere, ctrl.State.Editor.Text.Trim());

        // Another change on disk is a new question.
        WriteExternally(h.Path, External + " // again");
        await h.Tabs.CheckFilesChangedOnDiskAsync();
        h.AssertAskedToReload(2);
    }

    [AvaloniaFact]
    public async Task ADirtyTab_Should_ReloadWhenTheUserSaysSo()
    {
        using var h = new Harness();
        var (vm, ctrl) = await h.OpenAsync();
        ctrl.State.Editor.Text = EditedHere + "\n";
        WriteExternally(h.Path, External);
        h.AnswerReload(true);

        await h.Tabs.CheckFilesChangedOnDiskAsync();

        h.AssertAskedToReload(1);
        Assert.False(vm.IsDirty);
        Assert.Equal(External, ctrl.State.Editor.Text.Trim());
    }

    // The header follows the file too, without the toolbar's sync writing anything back.
    [AvaloniaFact]
    public async Task AReload_Should_TakeTheNewHeader_WithoutSaving()
    {
        using var h = new Harness();
        var (vm, ctrl) = await h.OpenAsync();
        WriteExternally(h.Path, "void Main() { }", MdRunKind.Program);
        var onDisk = File.ReadAllBytes(h.Path);

        await h.Tabs.CheckFilesChangedOnDiskAsync();

        Assert.Equal(nameof(MdRunKind.Program), vm.SelectedKind);
        Assert.Equal(MdRunKind.Program, ctrl.State.MdRunDocument!.FrontMatter.Kind);
        Assert.Equal("void Main() { }", ctrl.State.Editor.Text.Trim());
        Assert.False(vm.IsDirty);
        Assert.Equal(onDisk, File.ReadAllBytes(h.Path));
    }

    // A script now in another language opens in a new tab of that language, in the same place.
    [AvaloniaFact]
    public async Task AScriptInAnotherLanguage_Should_ReopenInPlace()
    {
        using var h = new Harness();
        var (vm, _) = await h.OpenAsync();
        h.MainVm.OpenTab(null);
        WriteExternally(h.Path, "Console.WriteLine(\"vb\")", language: MdRunLanguage.Vb);

        await h.Tabs.CheckFilesChangedOnDiskAsync();

        var reopened = h.MainVm.Tabs[0];
        Assert.NotSame(vm, reopened);
        Assert.Equal(h.Path, reopened.FilePath);
        Assert.Equal(2, h.MainVm.Tabs.Count);
        await TestWait.WhileAsync(() => h.Tabs.ControlForTests(reopened) is null);
        Assert.Equal(MdRunLanguage.Vb, h.Tabs.ControlForTests(reopened)!.State.MdRunDocument!.FrontMatter.Language);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RevertToSaved_Should_AskForADirtyTab(bool revert)
    {
        using var h = new Harness();
        var (vm, ctrl) = await h.OpenAsync();
        ctrl.State.Editor.Text = EditedHere + "\n";
        h.Windows.Setup(w => w.ShowRevertFileDialogAsync("demo.inv")).ReturnsAsync(revert);

        await h.Tabs.RevertActiveAsync();

        h.Windows.Verify(w => w.ShowRevertFileDialogAsync("demo.inv"), Times.Once);
        Assert.Equal(revert ? Original : EditedHere, ctrl.State.Editor.Text.Trim());
        Assert.Equal(!revert, vm.IsDirty);
    }

    [AvaloniaFact]
    public async Task RevertToSaved_Should_NotAskForACleanTab()
    {
        using var h = new Harness();
        var (vm, ctrl) = await h.OpenAsync();
        WriteExternally(h.Path, External);

        await h.Tabs.RevertActiveAsync();

        h.Windows.Verify(w => w.ShowRevertFileDialogAsync(It.IsAny<string>()), Times.Never);
        Assert.Equal(External, ctrl.State.Editor.Text.Trim());
        Assert.False(vm.IsDirty);
    }

    [AvaloniaFact]
    public async Task Reload_Should_PreserveLiteralCaretMarker()
    {
        using var h = new Harness();
        var (_, ctrl) = await h.OpenAsync();
        const string code = "Console.WriteLine(\"${0}\");";
        WriteExternally(h.Path, code);
        await h.Tabs.CheckFilesChangedOnDiskAsync();
        Assert.Equal(code, ctrl.State.Editor.Text.Trim());
    }

    [AvaloniaFact]
    public async Task Reload_Should_NotReplaceTabThatStartedRunningDuringPrompt()
    {
        using var h = new Harness();
        var (vm, ctrl) = await h.OpenAsync();
        ctrl.State.Editor.Text = EditedHere + "\n";
        WriteExternally(h.Path, External);
        h.Windows.Setup(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>()))
            .Returns(() => { vm.RunState = RunState.Run; return Task.FromResult(true); });
        await h.Tabs.CheckFilesChangedOnDiskAsync();
        Assert.Equal(EditedHere, ctrl.State.Editor.Text.Trim());
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reload_Should_IgnoreDecisionAfterBaselineChanges(bool accept)
    {
        using var h = new Harness();
        var (vm, ctrl) = await h.OpenAsync();
        ctrl.State.Editor.Text = EditedHere + "\n";
        WriteExternally(h.Path, External);
        h.Windows.Setup(w => w.ShowReloadChangedFileDialogAsync(It.IsAny<string>()))
            .Returns(() => { vm.DiskState = ScriptFileState.Read(h.Path); return Task.FromResult(accept); });
        await h.Tabs.CheckFilesChangedOnDiskAsync();
        Assert.Equal(EditedHere, ctrl.State.Editor.Text.Trim());
        Assert.Null(vm.DeclinedDiskState);
    }

    [AvaloniaFact]
    public async Task Revert_Should_LeaveTabAloneWhenSaveStartsDuringPrompt()
    {
        using var h = new Harness();
        var (vm, ctrl) = await h.OpenAsync();
        ctrl.State.Editor.Text = EditedHere + "\n";
        var saving = new TaskCompletionSource<InPlaceSaveResult>();
        h.Windows.Setup(w => w.ShowRevertFileDialogAsync(It.IsAny<string>()))
            .Returns(() => { vm.PendingSave = saving.Task; return Task.FromResult(true); });
        await h.Tabs.RevertActiveAsync();
        Assert.Equal(EditedHere, ctrl.State.Editor.Text.Trim());
        vm.PendingSave = null;
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAndReload_Should_KeepLiteralMarkerInValidAndRawFiles(bool raw)
    {
        using var h = new Harness();
        const string code = "Console.WriteLine(\"${0}\");";
        File.WriteAllText(h.Path, raw ? code : MdRunWriter.NewFile(code + "\n", "Demo"));
        var (_, ctrl) = await h.OpenAsync();
        Assert.Equal(code, ctrl.State.Editor.Text.Trim());
        File.WriteAllText(h.Path, raw ? code + " // changed" : MdRunWriter.NewFile(code + " // changed\n", "Demo"));
        await h.Tabs.CheckFilesChangedOnDiskAsync();
        Assert.Equal(code + " // changed", ctrl.State.Editor.Text.Trim());
    }

    // ── The dialog ───────────────────────────────────────────────────────────

    private static async Task<bool?> AnswerAsync(ReloadFileDialog dialog, Action<ReloadFileDialog> answer)
    {
        var owner = new Window();
        owner.Show();
        var result = dialog.ShowDialog<bool?>(owner);
        Dispatcher.UIThread.RunJobs();
        answer(dialog);
        Dispatcher.UIThread.RunJobs();
        var choice = await result;
        owner.Close();
        return choice;
    }

    [AvaloniaFact]
    public void TheDialog_Should_NameTheFile_AndKeepTheChangesByDefault()
    {
        var dialog = ReloadFileDialog.ChangedOnDisk("demo.inv");
        var buttons = dialog.GetLogicalDescendants().OfType<Button>().ToDictionary(b => (string)b.Content!);

        Assert.Contains("\"demo.inv\" was changed by another program", dialog.FindControl<TextBlock>("MessageBlock")!.Text);
        Assert.Equal(["Reload", "Keep My Changes"], buttons.Keys);
        Assert.False(buttons["Reload"].IsDefault);
        Assert.True(buttons["Keep My Changes"].IsDefault);
        Assert.True(buttons["Keep My Changes"].IsCancel);
    }

    [AvaloniaFact]
    public async Task TheDialog_Should_ReloadOnlyFromItsButton()
    {
        Assert.True(await AnswerAsync(ReloadFileDialog.Revert("demo.inv"),
            d => d.FindControl<Button>("ReloadButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        Assert.False(await AnswerAsync(ReloadFileDialog.Revert("demo.inv"),
            d => d.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None)));
        Assert.False(await AnswerAsync(ReloadFileDialog.Revert("demo.inv"),
            d => d.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None)));
    }
}
