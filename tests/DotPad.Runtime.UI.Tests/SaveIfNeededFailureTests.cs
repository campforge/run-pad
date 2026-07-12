using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using DotPad.Avalonia.Themes;
using DotPad.Contracts;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Services;
using DotPad.Services.BuildCache;
using DotPad.Services.Preferences;
using DotPad.UserControls;
using DotPad.UserControls.Models;
using Moq;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #48: EditorTabItemControl.SaveIfNeeded() used to swallow every write failure (disk full,
// permissions, file deleted/locked externally) with a bare `catch { }` — the user had no way to
// find out their edits weren't persisted. Fixed by surfacing IOException/UnauthorizedAccessException
// through the same ExecStatusText/Color the run status uses.
public class SaveIfNeededFailureTests
{
    [AvaloniaFact]
    public void SaveIfNeeded_Should_SurfaceFailure_ViaExecStatus_InsteadOfSilentlySwallowingIt()
    {
        if (OperatingSystem.IsWindows()) return; // the Unix file-mode trick below doesn't apply

        var tmpDir = Directory.CreateTempSubdirectory("dotpad-save-fail-test").FullName;
        var filePath = Path.Combine(tmpDir, "demo.rpad");
        try
        {
            File.WriteAllText(filePath, RpadWriter.NewFile("Demo"));

            var prefs = new Mock<IPreferencesService>();
            prefs.Setup(p => p.Editor).Returns(new EditorSettings());
            prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
            prefs.Setup(p => p.Layout).Returns(new LayoutSettings());

            var theme = new Mock<IThemeService>();
            theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
                new InterfaceTheme("Test", ThemeVariant.Dark, true),
                new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

            var vm = new EditorTabItemViewModel(filePath, [], prefs.Object);
            var registry = new Mock<ILanguageHostRegistry>();
            var buildCache = new Mock<IBuildCacheService>();

            var ctrl = new EditorsTabItemControl(vm, theme.Object, registry.Object, prefs.Object,
                intelliSense: null, new CSharpReferenceService(), buildCache.Object, editorBackground: null);
            var host = new Window { Content = ctrl };
            host.Show();

            // Remove write permission so the next write genuinely fails, instead of just asserting
            // the catch clause's type list matches what File.WriteAllText can actually throw.
            File.SetUnixFileMode(filePath, UnixFileMode.None);

            // Act
            ctrl.SaveIfNeeded();

            // Assert
            Assert.Contains("Save failed", ctrl.State.ExecStatusText);
            Assert.Equal("DotPad.Status.ErrorBrush", ctrl.State.ExecStatusColor);
        }
        finally
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
