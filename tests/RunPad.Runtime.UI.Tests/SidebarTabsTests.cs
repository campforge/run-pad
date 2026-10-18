using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Examples;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using RunPad.Windows;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// The sidebar's Queries / Examples tabs in a real main window: no "FILES" heading any more, switching
// keeps the file tree as it was, and an example clicked in the list opens as an untitled, unsaved copy
// that Save turns into a new file through Save As, leaving the bundled original unchanged.
public sealed class SidebarTabsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("runpad-sidebar").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private (MainWindow Window, MainWindowViewModel Vm, Mock<IWindowService> WindowService) CreateMainWindow()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(_folder);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new FileSystemItem("first.rpad", false, Path.Combine(_folder, "first.rpad"), null),
            new FileSystemItem("second.rpad", false, Path.Combine(_folder, "second.rpad"), null),
        ]);

        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

        var windowService = new Mock<IWindowService>();
        var vm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: windowService.Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());
        var editorTabs = new EditorTabsControl(
            theme.Object, windowService.Object, new Mock<ILanguageHostRegistry>().WithCSharpIntelliSense(() => new Mock<IIntelliSenseService>().Object), prefs.Object,
            new DotnetRuntimeReferenceLocator(), new Mock<IBuildCacheService>().Object);

        var window = new MainWindow(prefs.Object, editorTabs, vm) { Width = 1000, Height = 600 };
        window.Show();
        Settle();
        return (window, vm, windowService);
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<TextEditor> WaitForEditorAsync(Window window, Func<EditorsTabItemControl, bool> which)
    {
        for (var i = 0; i < 100; i++)
        {
            Settle();
            if (window.GetVisualDescendants().OfType<EditorsTabItemControl>().FirstOrDefault(which) is { } control
                && control.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault() is { } editor
                && editor.Document.TextLength > 0)
                return editor;
            await Task.Delay(20);
        }
        throw new TimeoutException("The example's editor was not created.");
    }

    private static T Named<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle();
    }

    [AvaloniaFact]
    public void TheSidebar_Should_HaveQueriesAndExamplesTabs_AndNoFilesHeading()
    {
        var (window, _, _) = CreateMainWindow();

        var tabs = Named<TabStrip>(window, "SidebarTabs");
        Assert.Equal(["Queries", "Examples"], tabs.Items.OfType<TabStripItem>().Select(i => i.Content));
        Assert.Equal(0, tabs.SelectedIndex);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "FILES");
    }

    // Clicking the Examples tab shows the list; back on Queries the tree keeps its selection and the
    // editor tabs are the same.
    [AvaloniaFact]
    public void SwitchingTabs_Should_KeepTheTreesSelection_AndTheEditorTabs()
    {
        var (window, vm, _) = CreateMainWindow();
        var tree = window.GetVisualDescendants().OfType<TreeView>().Single(t => t.Name == "FileTree");
        tree.SelectedItem = vm.FileTree.Nodes[1];
        Settle();
        var editorTabs = vm.Tabs.ToList();
        var tabItems = Named<TabStrip>(window, "SidebarTabs").GetVisualDescendants().OfType<TabStripItem>().ToList();

        Click(window, tabItems[1]);
        Assert.Equal(MainWindowViewModel.ExamplesTab, vm.SidebarTab);
        Assert.True(Named<ListBox>(window, "ExampleList").IsEffectivelyVisible);
        Assert.False(tree.IsEffectivelyVisible);

        Click(window, tabItems[0]);
        Assert.Equal(MainWindowViewModel.QueriesTab, vm.SidebarTab);
        Assert.True(tree.IsEffectivelyVisible);
        Assert.Same(vm.FileTree.Nodes[1], tree.SelectedItem);
        Assert.Equal(editorTabs, vm.Tabs);
    }

    // A real click on an example: an untitled, unsaved tab with the example's code; clicking it again
    // reuses that tab. Save goes through Save As (suggesting the example's title) and writes the whole
    // document — prose included — to the chosen path; the bundled original stays as it was.
    [AvaloniaFact]
    public async Task ClickingAnExample_Should_OpenAnUnsavedCopy_ThatSaveAsWritesElsewhere()
    {
        var (window, vm, windowService) = CreateMainWindow();
        var example = ExampleScripts.All[0];
        var original = example.Text;
        vm.SidebarTab = MainWindowViewModel.ExamplesTab;
        Settle();
        var list = Named<ListBox>(window, "ExampleList");
        var item = list.GetVisualDescendants().OfType<ListBoxItem>().First();

        Click(window, item);
        var editor = await WaitForEditorAsync(window, c => c.DataContext is EditorTabItemViewModel { Example: not null });

        var tab = vm.ActiveTab!;
        Assert.Same(example, tab.Example);
        Assert.True(tab.IsDirty);
        Assert.Null(tab.FilePath);
        Assert.Equal(RpadParser.TryParse(original)!.RunCode, editor.Text);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder)); // nothing written, nothing run

        var count = vm.Tabs.Count;
        Click(window, item);
        Assert.Equal(count, vm.Tabs.Count);
        Assert.Same(tab, vm.ActiveTab);

        var target = Path.Combine(_folder, "my copy.rpad");
        windowService.Setup(w => w.ShowSaveFileDialogAsync("Quick start", _folder)).ReturnsAsync(target);
        editor.Document.Insert(editor.Document.TextLength, "\n// edited");
        tab.SaveAsCommand.Execute().Subscribe();
        for (var i = 0; i < 100 && !File.Exists(target); i++) { Settle(); await Task.Delay(20); }

        var saved = await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken);
        Assert.Contains("# Quick start", saved);
        Assert.Contains("// edited", saved);
        Assert.Equal(target, tab.FilePath);
        Assert.False(tab.IsDirty);
        Assert.Equal(original, ExampleScripts.All[0].Text);
    }
}
