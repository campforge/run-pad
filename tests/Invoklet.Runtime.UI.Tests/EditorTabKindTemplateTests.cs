using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.RPad;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.VB;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.Windows.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// A Program script starts with a Main skeleton, and switching a script to Program wraps its code in
// Main as one undoable edit.
public class EditorTabKindTemplateTests
{
    [AvaloniaFact]
    public async Task ANewProgramScript_Should_StartWithMain_AndTheCaretInsideIt()
    {
        var (mainVm, editorTabs, tmpDir) = CreateTabs();
        try
        {
            mainVm.NewRpadFile(RpadLanguage.CSharp, RpadKind.Program);
            var ctrl = await ActiveControlAsync(mainVm, editorTabs);
            var editor = ctrl.State.Editor;

            Assert.Equal("void Main()\n{\n    \n}\n\n// Other methods, fields and types used by Main go here.\n", editor.Text);
            Assert.Equal("void Main()\n{\n    ".Length, editor.CaretOffset);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ANewStatementsScript_Should_StayEmpty()
    {
        var (mainVm, editorTabs, tmpDir) = CreateTabs();
        try
        {
            mainVm.NewRpadFile(RpadLanguage.CSharp, RpadKind.Statements);
            var ctrl = await ActiveControlAsync(mainVm, editorTabs);

            Assert.Equal("", ctrl.State.Editor.Text);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task SwitchingToProgram_Should_WrapTheCodeInMain_AndUndoInOneStep()
    {
        var (mainVm, editorTabs, tmpDir) = CreateTabs();
        try
        {
            const string code = "var x = 21;\nConsole.WriteLine(x * 2);\n";
            var filePath = Path.Combine(tmpDir, "demo.rpad");
            File.WriteAllText(filePath, RpadWriter.NewFile(code, "Demo", kind: RpadKind.Statements));
            mainVm.OpenTab(filePath);
            var ctrl = await ActiveControlAsync(mainVm, editorTabs);
            var editor = ctrl.State.Editor;
            Assert.Equal(code, editor.Text);

            mainVm.ActiveTab!.SelectedKind = nameof(RpadKind.Program);

            Assert.Equal(RpadKind.Program, ctrl.State.RpadDocument!.FrontMatter.Kind);
            Assert.Equal(
                "void Main()\n{\n    var x = 21;\n    Console.WriteLine(x * 2);\n}\n\n// Other methods, fields and types used by Main go here.\n",
                editor.Text);

            Assert.True(editor.Undo());
            Assert.Equal(code, editor.Text);

            // Switching back leaves the code alone.
            Assert.True(editor.Redo());
            var wrapped = editor.Text;
            mainVm.ActiveTab!.SelectedKind = nameof(RpadKind.Statements);
            Assert.Equal(wrapped, editor.Text);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    private static async Task<EditorsTabItemControl> ActiveControlAsync(MainWindowViewModel mainVm, EditorTabsControl editorTabs)
    {
        await TestWait.WhileAsync(() => mainVm.ActiveTab?.FrontMatter is null);
        var fileTabControl = editorTabs.GetVisualDescendants().OfType<TabControl>()
            .First(t => t.Name == "FileTabControl");
        return (EditorsTabItemControl)((TabItem)fileTabControl.SelectedItem!).Content!;
    }

    private static (MainWindowViewModel, EditorTabsControl, string) CreateTabs()
    {
        var tmpDir = Directory.CreateTempSubdirectory("Invoklet-kind-template-test").FullName;

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(tmpDir);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

        var registry = new LanguageHostRegistry();
        registry.Register(new CSharpLanguageHostFactory(NullLoggerFactory.Instance).WithIntelliSense(() => new Mock<IIntelliSenseService>().Object));
        registry.Register(new VbLanguageHostFactory(NullLoggerFactory.Instance));

        var mainVm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object,
            prefs: prefs.Object,
            languageRegistry: registry);

        var editorTabs = new EditorTabsControl(
            theme.Object, new Mock<IWindowService>().Object, registry, prefs.Object,
            new Mock<IBuildCacheService>().Object);

        new Window { Content = editorTabs }.Show();
        editorTabs.Attach(mainVm); // opens the initial blank tab
        return (mainVm, editorTabs, tmpDir);
    }
}
