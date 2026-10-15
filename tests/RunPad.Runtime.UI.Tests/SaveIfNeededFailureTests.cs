using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// EditorTabItemControl.SaveIfNeeded() used to swallow every write failure (disk full,
// permissions, file deleted/locked externally) with a bare `catch { }` — the user had no way to
// find out their edits weren't persisted. Fixed by surfacing IOException/UnauthorizedAccessException
// through the same ExecStatusText/Color the run status uses.
public class SaveIfNeededFailureTests
{
    [AvaloniaFact]
    public void SaveIfNeeded_Should_SurfaceFailure_ViaExecStatus_InsteadOfSilentlySwallowingIt()
    {
        if (OperatingSystem.IsWindows()) return; // the Unix file-mode trick below doesn't apply

        var tmpDir = Directory.CreateTempSubdirectory("runpad-save-fail-test").FullName;
        var filePath = Path.Combine(tmpDir, "demo.rpad");
        try
        {
            File.WriteAllText(filePath, RpadWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo"));

            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());

            var theme = new Mock<IThemeService>();
            theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
                new InterfaceTheme("Test", ThemeVariant.Dark, true),
                new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

            var vm = new EditorTabItemViewModel(filePath, [], prefs.Object, new Mock<IEditorTabActions>().Object);
            var registry = new Mock<ILanguageHostRegistry>();
            var buildCache = new Mock<IBuildCacheService>();

            var ctrl = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object, registry.Object, prefs.Object,
                intelliSense: null, new CSharpReferenceService(), buildCache.Object);
            var host = new Window { Content = ctrl };
            host.Show();

            // Remove write permission so the next write genuinely fails, instead of just asserting
            // the catch clause's type list matches what File.WriteAllText can actually throw.
            File.SetUnixFileMode(filePath, UnixFileMode.None);

            // Act
            ctrl.SaveIfNeeded();

            // Assert
            Assert.Contains("Save failed", ctrl.State.ExecStatusText);
            Assert.Equal("RunPad.Status.ErrorBrush", ctrl.State.ExecStatusColor);
        }
        finally
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
