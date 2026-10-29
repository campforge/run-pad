using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.RPad;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// The main window is ready to be revealed (IStartupWindow.Ready) only once every restored tab has its
// header and the active one is selected: tabs are added one by one, each after its language
// services exist, and the splash covers that.
public class StartupWorkspaceReadyTests
{
    [AvaloniaFact]
    public async Task Ready_Should_WaitForEveryRestoredTabsHeader()
    {
        var root = Directory.CreateTempSubdirectory("Invoklet-startup-ready-test").FullName;
        var paths = new[] { "a.rpad", "b.rpad", "c.rpad" }.Select(n => Path.Combine(root, n)).ToArray();
        foreach (var path in paths)
            File.WriteAllText(path, RpadWriter.NewFile("Console.WriteLine(1);\n", Path.GetFileNameWithoutExtension(path)));

        var session = new SessionSettings { OpenTabs = [.. paths], ActiveTabPath = paths[1] };
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(session);
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(root);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

        // The last tab's language services take a while: its header can't be there before they are.
        using var lastTab = new ManualResetEventSlim();
        var created = 0;
        var editorTabs = new EditorTabsControl(theme.Object, new Mock<IWindowService>().Object,
            new Mock<ILanguageHostRegistry>().WithCSharpIntelliSense(() =>
            {
                if (Interlocked.Increment(ref created) == paths.Length) lastTab.Wait(TimeSpan.FromSeconds(10));
                return new Mock<IIntelliSenseService>().Object;
            }),
            prefs.Object, new Mock<IBuildCacheService>().Object);
        var mainVm = new MainWindowViewModel(sdkProviders: [], fileSystemService: fileSystem.Object,
            windowService: new Mock<IWindowService>().Object, prefs: prefs.Object, languageRegistry: new LanguageHostRegistry());
        var window = new MainWindow(prefs.Object, editorTabs, mainVm);
        var ready = ((IStartupWindow)window).Ready;
        try
        {
            window.Show();
            for (var i = 0; i < 50 && Volatile.Read(ref created) < paths.Length; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
            Dispatcher.UIThread.RunJobs();
            Assert.False(ready.IsCompleted);

            lastTab.Set();
            await ready.WaitAsync(TimeSpan.FromSeconds(10));
            var tabs = window.FindControl<ContentControl>("EditorTabsHost")!.Content as EditorTabsControl;
            var fileTabs = tabs!.FindControl<TabControl>("FileTabControl")!;
            Assert.Equal(paths.Length, fileTabs.ItemCount);
            Assert.Same(mainVm.ActiveTab, mainVm.Tabs.Single(t => t.FilePath == paths[1]));
            Assert.Same(fileTabs.Items[1], fileTabs.SelectedItem);
        }
        finally
        {
            window.Close();
            window.Dispose();
            mainVm.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
