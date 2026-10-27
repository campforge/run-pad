using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Format.RPad;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Each tab asks its registered language for its own editor services, off the UI thread, and owns
// what it gets; a tab closed before they arrive cancels them and disposes whatever still comes back.
public sealed class EditorTabsEditorServicesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("runpad-editor-services").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A registered language the test controls: each creation returns a new fake service, optionally
    // only once the test releases it.
    private sealed class FakeLanguage(string id, bool hasEditorServices = true) : ILanguageHostFactory
    {
        public readonly List<Mock<IIntelliSenseService>> Created = [];
        public readonly List<CancellationToken> Tokens = [];
        public readonly List<bool> OnUiThread = [];
        public ManualResetEventSlim? Gate { get; set; }
        public bool HonourCancellation { get; set; }
        private readonly Lock _lock = new();

        public string Language => id;
        public string DisplayName => id;
        public LanguageHostCapabilities Capabilities => LanguageHostCapabilities.IntelliSense;
        public IReadOnlyDictionary<string, string> Snippets { get; } = new Dictionary<string, string>();
        public EditorLanguageProfile EditorProfile { get; } = new();
        public ILanguageHost Create(ILanguageHostOptions? options = null) => throw new NotSupportedException();

        public IIntelliSenseService? CreateIntelliSense(CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Tokens.Add(cancellationToken);
                OnUiThread.Add(Dispatcher.UIThread.CheckAccess());
            }
            Gate?.Wait(TimeSpan.FromSeconds(30));
            if (HonourCancellation) cancellationToken.ThrowIfCancellationRequested();
            if (!hasEditorServices) return null;
            var service = new Mock<IIntelliSenseService>();
            lock (_lock) Created.Add(service);
            return service.Object;
        }
    }

    private (EditorTabsControl Tabs, MainWindowViewModel Shell, Window Host) Open(params ILanguageHostFactory[] languages)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Debugger).Returns(new DebuggerSettings());
        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(_root);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

        var registry = new LanguageHostRegistry();
        foreach (var language in languages) registry.Register(language);
        var shell = new MainWindowViewModel([], fileSystem.Object, new Mock<IWindowService>().Object, prefs.Object, new LanguageHostRegistry());
        var tabs = new EditorTabsControl(theme.Object, new Mock<IWindowService>().Object, registry, prefs.Object,
            new Mock<IBuildCacheService>().Object);
        var host = new Window { Width = 900, Height = 700, Content = tabs };
        host.Show();
        return (tabs, shell, host);
    }

    private static async Task<EditorsTabItemControl> ShownAsync(EditorTabsControl tabs, EditorTabItemViewModel vm)
    {
        await TestWait.WhileAsync(() => tabs.ControlForTests(vm) is null);
        return tabs.ControlForTests(vm) ?? throw new InvalidOperationException("the tab never opened");
    }

    [AvaloniaFact]
    public async Task ARegisteredLanguage_Should_GiveEveryTabItsOwnEditorServices_OffTheUiThread()
    {
        var csharp = new FakeLanguage("csharp");
        var (tabs, shell, host) = Open(csharp);
        try
        {
            tabs.Attach(shell); // the initial blank C# tab
            shell.NewRpadFile(RpadLanguage.CSharp);

            var first = await ShownAsync(tabs, shell.Tabs[0]);
            var second = await ShownAsync(tabs, shell.Tabs[1]);

            Assert.Equal(2, csharp.Created.Count);
            Assert.Same(csharp.Created[0].Object, first.IntelliSenseForTests);
            Assert.Same(csharp.Created[1].Object, second.IntelliSenseForTests);
            Assert.NotSame(first.IntelliSenseForTests, second.IntelliSenseForTests);
            Assert.All(csharp.OnUiThread, Assert.False);
        }
        finally { host.Close(); }
    }

    // A language without editor services: the tab opens, with none, and nothing to dispose.
    [AvaloniaFact]
    public async Task ATabInALanguageWithoutEditorServices_Should_OpenWithNone()
    {
        var vb = new FakeLanguage("vb", hasEditorServices: false);
        var (tabs, shell, host) = Open(new FakeLanguage("csharp"), vb);
        try
        {
            tabs.Attach(shell);
            shell.NewRpadFile(RpadLanguage.Vb);

            var ctrl = await ShownAsync(tabs, shell.Tabs[^1]);

            Assert.Null(ctrl.IntelliSenseForTests);
            Assert.Single(vb.Tokens);
        }
        finally { host.Close(); }
    }

    // Closed while its services are still being created: the creation is cancelled, the tab is never
    // built, and a service that is returned anyway is disposed exactly once. A language that honours
    // the cancellation returns nothing to dispose.
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingATab_WhileItsServicesAreCreated_Should_CancelThem_AndDisposeWhatArrivesOnce(bool honourCancellation)
    {
        using var gate = new ManualResetEventSlim();
        var csharp = new FakeLanguage("csharp") { Gate = gate, HonourCancellation = honourCancellation };
        var (tabs, shell, host) = Open(csharp);
        try
        {
            tabs.Attach(shell);
            var vm = shell.Tabs[0];
            await TestWait.WhileAsync(() => csharp.Tokens.Count == 0);

            shell.Tabs.Remove(vm);
            Assert.True(csharp.Tokens[0].IsCancellationRequested);
            gate.Set();
            await TestWait.WhileAsync(() => !honourCancellation && csharp.Created.Count == 0);
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(tabs.ControlForTests(vm));
            Assert.Equal(honourCancellation ? 0 : 1, csharp.Created.Count);
            foreach (var created in csharp.Created) created.Verify(s => s.Dispose(), Times.Once);
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public async Task DisposingTheTabs_WhileServicesAreCreated_Should_DisposeWhatArrivesOnce()
    {
        using var gate = new ManualResetEventSlim();
        var csharp = new FakeLanguage("csharp") { Gate = gate };
        var (tabs, shell, host) = Open(csharp);
        try
        {
            tabs.Attach(shell);
            await TestWait.WhileAsync(() => csharp.Tokens.Count == 0);

            tabs.Dispose();
            Assert.True(csharp.Tokens[0].IsCancellationRequested);
            gate.Set();
            await TestWait.WhileAsync(() => csharp.Created.Count == 0);
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(tabs.ControlForTests(shell.Tabs[0]));
            csharp.Created[0].Verify(s => s.Dispose(), Times.Once);
        }
        finally { host.Close(); }
    }

    // An open tab owns its services: closing it disposes them once, and a second Dispose doesn't again.
    [AvaloniaFact]
    public async Task ClosingAnOpenTab_Should_DisposeItsServicesOnce()
    {
        var csharp = new FakeLanguage("csharp");
        var (tabs, shell, host) = Open(csharp);
        try
        {
            tabs.Attach(shell);
            var vm = shell.Tabs[0];
            var ctrl = await ShownAsync(tabs, vm);

            shell.Tabs.Remove(vm);
            ctrl.Dispose();

            csharp.Created[0].Verify(s => s.Dispose(), Times.Once);
        }
        finally { host.Close(); }
    }
}
