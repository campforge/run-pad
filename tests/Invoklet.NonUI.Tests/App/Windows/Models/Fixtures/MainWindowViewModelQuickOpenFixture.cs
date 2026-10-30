using Invoklet.Contracts;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Services.QuickOpen;
using Invoklet.Windows.Models;
using Moq;

namespace Invoklet.NonUI.Tests.App.Windows.Models.Fixtures;

// A view model over a scripts folder on the real disk (opening a script reads the file) with the window service and the file tree faked:
// the fixture says what the Quick Open box returns and keeps what it was shown.
internal sealed class MainWindowViewModelQuickOpenFixture : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("quickopen");

    public Mock<IWindowService> Windows { get; } = new();

    public IReadOnlyList<QuickOpenEntry>? Shown { get; private set; }

    public string ScriptPath { get; }

    public MainWindowViewModelQuickOpenFixture()
    {
        ScriptPath = Path.Combine(_folder.FullName, "Report.inv");
        File.WriteAllText(ScriptPath, "");
    }

    public MainWindowViewModel CreateSut(string? choice)
    {
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(_folder.FullName);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FileSystemItem("Report.inv", false, ScriptPath, null)]);
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        Windows.Setup(w => w.ShowQuickOpenAsync(It.IsAny<IReadOnlyList<QuickOpenEntry>>()))
            .Callback<IReadOnlyList<QuickOpenEntry>>(entries => Shown = entries)
            .ReturnsAsync(choice);
        return new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: Windows.Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry());
    }

    public void Dispose() => _folder.Delete(recursive: true);
}
