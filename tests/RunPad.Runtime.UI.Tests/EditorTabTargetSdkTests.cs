using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// The front matter's targetSdk is the source of truth: the toolbar shows it ("latest (…)" for
// latest), and picking an SDK there writes it back.
public class EditorTabTargetSdkTests
{
    private static readonly DotnetSdk Net10 = new(".NET", "net10.0", "10.0.0", ".NET 10.0.100");
    private static readonly DotnetSdk Net8 = new(".NET", "net8.0", "8.0.0", ".NET 8.0.400");

    [AvaloniaFact]
    public async Task ALatestScript_Should_ShowTheLatestChoice_AndPickingAVersion_Should_WriteIt()
    {
        var (mainVm, editorTabs, tmpDir) = CreateTabs();
        try
        {
            var path = WriteScript(tmpDir, "latest");
            mainVm.OpenTab(path);
            var ctrl = await ActiveControlAsync(mainVm, editorTabs);
            var tab = mainVm.ActiveTab!;

            Assert.Same(tab.LatestSdk, tab.SelectedSdk);
            Assert.Equal("latest (.NET 10.0.100)", tab.SelectedSdk!.Description);
            Assert.False(tab.IsDirty);

            tab.SelectedSdk = tab.SdkChoices.Single(s => s.Description == ".NET 8.0.400");
            Assert.Equal("net8.0", ctrl.State.RpadDocument!.FrontMatter.TargetSdk.ToString());
            Assert.Contains("targetSdk: net8.0", File.ReadAllText(path));

            tab.SelectedSdk = tab.LatestSdk;
            Assert.Equal("latest", ctrl.State.RpadDocument!.FrontMatter.TargetSdk.ToString());
            Assert.Contains("targetSdk: latest", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task AnInstalledVersion_Should_BeSelected_WithoutChangingTheFile()
    {
        var (mainVm, editorTabs, tmpDir) = CreateTabs();
        try
        {
            var path = WriteScript(tmpDir, "net8.0");
            mainVm.OpenTab(path);
            await ActiveControlAsync(mainVm, editorTabs);
            var tab = mainVm.ActiveTab!;

            Assert.Equal(".NET 8.0.400", tab.SelectedSdk!.Description);
            Assert.False(tab.IsDirty);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // A version that isn't installed runs on the newest SDK and says so — and opening the file
    // doesn't rewrite its targetSdk.
    [AvaloniaFact]
    public async Task AMissingVersion_Should_RunOnTheNewest_AndKeepTheFile()
    {
        var (mainVm, editorTabs, tmpDir) = CreateTabs();
        try
        {
            var path = WriteScript(tmpDir, "net6.0");
            mainVm.OpenTab(path);
            var ctrl = await ActiveControlAsync(mainVm, editorTabs);
            var tab = mainVm.ActiveTab!;

            Assert.Equal(".NET 10.0.100", tab.SelectedSdk!.Description);
            Assert.False(tab.IsDirty);
            Assert.Equal("net6.0", ctrl.State.RpadDocument!.FrontMatter.TargetSdk.ToString());
            Assert.Contains("targetSdk: net6.0", File.ReadAllText(path));
            var status = ctrl.FindControl<TextBlock>("ProjectInfoBlock")!.Text;
            Assert.Equal("Statements · net6.0 (not installed)", status);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    private static string WriteScript(string dir, string targetSdk)
    {
        var path = Path.Combine(dir, "demo.rpad");
        File.WriteAllText(path, RpadWriter.NewFile("Console.WriteLine(1);\n", "Demo", targetSdk));
        return path;
    }

    private static async Task<EditorsTabItemControl> ActiveControlAsync(MainWindowViewModel mainVm, EditorTabsControl editorTabs)
    {
        await TestWait.WhileAsync(() => mainVm.ActiveTab?.FrontMatter is null);
        EditorsTabItemControl? control = null;
        await TestWait.WhileAsync(() =>
        {
            var fileTabControl = editorTabs.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "FileTabControl");
            control = (fileTabControl.SelectedItem as TabItem)?.Content as EditorsTabItemControl;
            return control is null;
        });
        return control!;
    }

    private static (MainWindowViewModel, EditorTabsControl, string) CreateTabs()
    {
        var tmpDir = Directory.CreateTempSubdirectory("runpad-target-sdk-test").FullName;

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(tmpDir);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var sdks = new Mock<ISdkProvider>();
        sdks.Setup(s => s.Get(It.IsAny<string?>())).Returns([Net10, Net8]);

        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

        var registry = new LanguageHostRegistry();
        registry.Register(new CSharpLanguageHostFactory(NullLoggerFactory.Instance).WithIntelliSense(() => new Mock<IIntelliSenseService>().Object));

        var mainVm = new MainWindowViewModel(
            sdkProviders: [sdks.Object],
            fileSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object,
            prefs: prefs.Object,
            languageRegistry: registry);

        var editorTabs = new EditorTabsControl(
            theme.Object, new Mock<IWindowService>().Object, registry, prefs.Object,
            new Mock<IBuildCacheService>().Object);

        new Window { Content = editorTabs }.Show();
        editorTabs.Attach(mainVm);
        return (mainVm, editorTabs, tmpDir);
    }
}
