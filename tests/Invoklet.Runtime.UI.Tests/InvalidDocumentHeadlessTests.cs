using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Moq;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Contracts.Models;
using Invoklet.Format.RPad;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// A .rpad with malformed YAML used to open as a default C# document (no
// packages/references, empty code from a C# fence that wasn't there), and the next save persisted
// those defaults over the original metadata. Now the tab shows the whole file verbatim, saves it
// back verbatim, and refuses Run/Debug until the file is fixed and reopened.
public class InvalidDocumentHeadlessTests
{
    private const string MalformedVb =
        "---\nversion: 1.0\nlanguage: vb\ntargetSdk: net10.0\nkind: nonsense\npackages:\n  Newtonsoft.Json: 13.0.3\nreferences:\n  - ../Shared/Shared.vbproj\n---\n\n# VB demo\n\n```vb run\nConsole.WriteLine(1)\n```\n\nNotes after.\n";

    private sealed record Harness(
        MainWindow Window, MainWindowViewModel MainVm, Mock<IWindowService> WindowService,
        Mock<ILanguageHostRegistry> Registry, Mock<IIntelliSenseService> IntelliSense, string Root);

    private static readonly DotnetSdk Sdk = new("10.0.100", "net10.0", "10.0.0", ".NET 10.0");

    private static Harness CreateHarness()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-invalid-doc-ui-test").FullName;

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(root);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

        var buildCache = new Mock<IBuildCacheService>();
        buildCache.Setup(b => b.EnsureOutputDirectory(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Path.Combine(root, "build"));

        var windowService = new Mock<IWindowService>();
        var mainVm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: windowService.Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());

        // Create returns null on purpose: a run that gets past the guard then fails inside the host
        // call, which is enough to prove whether Run reached the language host at all.
        var registry = new Mock<ILanguageHostRegistry>();
        var intelliSense = new Mock<IIntelliSenseService>();
        var editorTabs = new EditorTabsControl(
            theme.Object, new Mock<IWindowService>().Object, registry.WithCSharpIntelliSense(() => intelliSense.Object), prefs.Object,
            buildCache.Object);

        var window = new MainWindow(prefs.Object, editorTabs, mainVm);
        window.Show();
        return new Harness(window, mainVm, windowService, registry, intelliSense, root);
    }

    private static async Task<(EditorTabItemViewModel Tab, EditorsTabItemControl Control)> OpenAsync(
        Harness h, string path)
    {
        h.MainVm.OpenTab(path);
        var tab = h.MainVm.Tabs.Single(t => t.FilePath == path);
        for (var i = 0; i < 100; i++)
        {
            var ctrl = h.Window.GetVisualDescendants().OfType<TabItem>()
                .Select(t => t.Content).OfType<EditorsTabItemControl>()
                .FirstOrDefault(c => ReferenceEquals(c.DataContext, tab));
            if (ctrl is not null) return (tab, ctrl);
            await Task.Delay(20);
        }
        throw new TimeoutException("The tab's control was not realized in time.");
    }

    // Same teardown as DocumentSafetyHeadlessTests.Cleanup, after the assertions: dirty flags are
    // dropped so closing the window neither asks nor writes, then the window (whose Dispose releases
    // the EditorTabsControl and its tab controls) and the view model are disposed.
    private static void Cleanup(Harness h)
    {
        try
        {
            foreach (var tab in h.MainVm.Tabs)
                tab.IsDirty = false;
            h.Window.Close();
            Assert.False(h.Window.IsVisible);
        }
        finally
        {
            h.Window.Dispose();
            h.MainVm.Dispose();
        }

        foreach (var file in Directory.GetFiles(h.Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(h.Root, recursive: true);
    }

    private static string WriteMalformed(Harness h)
    {
        var path = Path.Combine(h.Root, "bad.rpad");
        File.WriteAllText(path, MalformedVb);
        return path;
    }

    [AvaloniaFact]
    public async Task OpeningMalformedYaml_Should_ShowTheWholeOriginalFile_WithADiagnostic()
    {
        var h = CreateHarness();
        try
        {
            var (tab, ctrl) = await OpenAsync(h, WriteMalformed(h));

            Assert.Equal(MalformedVb, ctrl.State.Editor.Text);
            Assert.Null(ctrl.State.RpadDocument);
            Assert.Null(tab.FrontMatter);
            Assert.Contains("nonsense", ctrl.State.DocumentParseError);
            Assert.Equal("Invalid document", ctrl.State.ExecStatusText);
            Assert.False(tab.IsDirty);
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task SavingMalformedYaml_Should_WriteTheOriginalTextBackVerbatim_NotDefaultMetadata()
    {
        var h = CreateHarness();
        try
        {
            var path = WriteMalformed(h);
            var (tab, ctrl) = await OpenAsync(h, path);

            ctrl.SaveIfNeeded();
            Assert.Equal(MalformedVb, File.ReadAllText(path));

            // Save As goes through the same tab writer.
            var savedAs = Path.Combine(h.Root, "copy.rpad");
            h.WindowService.Setup(w => w.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(savedAs);
            await h.MainVm.SaveTabAsAsync(tab, tab.WriteToFile!);
            Assert.Equal(MalformedVb, File.ReadAllText(savedAs));
            Assert.Equal(savedAs, tab.FilePath);
        }
        finally { Cleanup(h); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunOrDebug_OnMalformedYaml_Should_BeRefused_WithoutReachingTheLanguageHost(bool debug)
    {
        var h = CreateHarness();
        try
        {
            var path = WriteMalformed(h);
            var (tab, ctrl) = await OpenAsync(h, path);

            await ctrl.RunAsync(debug, Sdk);

            h.Registry.Verify(r => r.Create(It.IsAny<string>(), It.IsAny<ExecutionRequest>()), Times.Never);
            Assert.Equal("Invalid document", ctrl.State.ExecStatusText);
            Assert.Equal(RunState.Idle, tab.RunState);
            Assert.Equal(MalformedVb, File.ReadAllText(path));
        }
        finally { Cleanup(h); }
    }

    // A numeric kind used to parse as an undefined RpadKind and run.
    [AvaloniaFact]
    public async Task Run_OnANumericUndeclaredKind_Should_BeRefused_AndKeepTheFile()
    {
        var h = CreateHarness();
        try
        {
            var path = Path.Combine(h.Root, "numeric.rpad");
            var original = MalformedVb.Replace("kind: nonsense", "kind: 99");
            File.WriteAllText(path, original);
            var (tab, ctrl) = await OpenAsync(h, path);

            Assert.Contains("Unsupported kind '99'", ctrl.State.DocumentParseError);
            Assert.Null(tab.FrontMatter);
            Assert.Equal(original, ctrl.State.Editor.Text);

            await ctrl.RunAsync(false, Sdk);
            ctrl.SaveIfNeeded();

            h.Registry.Verify(r => r.Create(It.IsAny<string>(), It.IsAny<ExecutionRequest>()), Times.Never);
            Assert.Equal("Invalid document", ctrl.State.ExecStatusText);
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally { Cleanup(h); }
    }

    [AvaloniaFact]
    public async Task FixingTheYaml_SavingAndReopening_Should_RestoreNormalMetadataAndRun()
    {
        var h = CreateHarness();
        try
        {
            var path = WriteMalformed(h);
            var (tab, ctrl) = await OpenAsync(h, path);

            ctrl.State.Editor.Text = MalformedVb.Replace("kind: nonsense", "kind: Statements");
            ctrl.SaveIfNeeded();
            Assert.False(tab.IsDirty);
            tab.CloseCommand.Execute().Subscribe();
            await Task.Delay(50);
            Assert.DoesNotContain(tab, h.MainVm.Tabs);

            var (reopened, reopenedCtrl) = await OpenAsync(h, path);

            Assert.Null(reopenedCtrl.State.DocumentParseError);
            var fm = reopened.FrontMatter!;
            Assert.Equal(RpadLanguage.Vb, fm.Language);
            Assert.Equal("13.0.3", fm.Packages["Newtonsoft.Json"].Version);
            Assert.Equal(["../Shared/Shared.vbproj"], fm.References);
            Assert.Equal("Console.WriteLine(1)\n", reopenedCtrl.State.Editor.Text);

            await reopenedCtrl.RunAsync(false, Sdk);
            h.Registry.Verify(r => r.Create("vb", It.IsAny<ExecutionRequest>()), Times.Once);
        }
        finally { Cleanup(h); }
    }
}
