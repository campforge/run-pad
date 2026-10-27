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

// A document's id: stays with its own file across saves; a file from before IDs gets one on its
// first save; a copy written elsewhere (Duplicate, Save As) is a separate document with its own,
// and only Save As moves the tab to that new identity. Real tabs on temporary files.
public class DocumentIdSaveTests
{
    private const string Legacy = "---\nversion: \"1.0\"\nlanguage: csharp\ntargetSdk: latest\nkind: Statements\n---\n\n# Old\n\n```csharp run\n1.Dump();\n```\n";

    private static (EditorsTabItemControl Ctrl, EditorTabItemViewModel Vm, Window Host) Open(string path)
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
        var vm = new EditorTabItemViewModel(path, [], prefs.Object, new Mock<IEditorTabActions>().Object);
        var ctrl = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object, new Mock<ILanguageHostRegistry>().Object,
            prefs.Object, intelliSense: null, new Mock<IBuildCacheService>().Object);
        var host = new Window { Content = ctrl };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        return (ctrl, vm, host);
    }

    private static Guid? IdIn(string path) => RpadParser.TryParse(File.ReadAllText(path))?.FrontMatter.Id;

    private static void InTemporaryFolder(Action<string> test)
    {
        var dir = Directory.CreateTempSubdirectory("runpad-id-test").FullName;
        try { test(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [AvaloniaFact]
    public void LegacyFile_Should_GetAnIdOnItsFirstSave_AndKeepIt() => InTemporaryFolder(dir =>
    {
        var path = Path.Combine(dir, "old.rpad");
        File.WriteAllText(path, Legacy);
        var (ctrl, vm, host) = Open(path);
        try
        {
            vm.WriteToFile!(path);
            var id = IdIn(path);
            vm.WriteToFile!(path);

            Assert.NotNull(id);
            Assert.Equal(id, IdIn(path));
        }
        finally { host.Close(); ctrl.Dispose(); }
    });

    [AvaloniaFact]
    public void Duplicate_Should_GetItsOwnId_AndLeaveTheOriginalsAlone() => InTemporaryFolder(dir =>
    {
        var path = Path.Combine(dir, "a.rpad");
        File.WriteAllText(path, RpadWriter.NewFile("1.Dump();\n"));
        var original = IdIn(path);
        var copy = Path.Combine(dir, "a copy.rpad");
        var (ctrl, vm, host) = Open(path);
        try
        {
            vm.WriteToFile!(copy);
            vm.WriteToFile!(path);

            Assert.NotNull(IdIn(copy));
            Assert.NotEqual(original, IdIn(copy));
            Assert.Equal(original, IdIn(path));
        }
        finally { host.Close(); ctrl.Dispose(); }
    });

    [AvaloniaFact]
    public void SaveAs_Should_MoveTheTabToTheNewId() => InTemporaryFolder(dir =>
    {
        var path = Path.Combine(dir, "a.rpad");
        File.WriteAllText(path, RpadWriter.NewFile("1.Dump();\n"));
        var original = IdIn(path);
        var moved = Path.Combine(dir, "b.rpad");
        var (ctrl, vm, host) = Open(path);
        try
        {
            vm.WriteToFile!(moved);
            vm.FilePath = moved;
            Dispatcher.UIThread.RunJobs();
            var written = IdIn(moved);
            vm.WriteToFile!(moved);

            Assert.NotEqual(original, written);
            Assert.Equal(written, IdIn(moved));
            Assert.Equal(original, IdIn(path));
        }
        finally { host.Close(); ctrl.Dispose(); }
    });
}
