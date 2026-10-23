using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Contracts;
using RunPad.Contracts.Connections;
using RunPad.Contracts.IntelliSense;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// A connection's description read elsewhere (Dependencies' Refresh, the Connections window, another
// tab's run) reaches an open tab that lists that connection, so its completion and diagnostics use
// the new classes without waiting for an edit; tabs without it are left alone.
public sealed class EditorTabConnectionSchemaChangeTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("runpad-tab-schema-change").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    private sealed class DescribedDriver : IConnectionSchemaDriver
    {
        public string Id => "described";
        public string DisplayName => "Described";
        public ConnectionCategory Category => ConnectionCategory.Grpc;
        public IReadOnlyList<ConnectionParam> Params { get; } = [];
        public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string>();
        public IReadOnlyList<string> Validate(RpadConnection connection) => [];
        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(true, ""));
        public ConnectionClientCode CreateClient(RpadConnection connection, ConnectionCodeContext context) =>
            new($"public sealed class {context.ClassName} {{ }}");
        public Task<ConnectionSchema> FetchSchemaAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, ConnectionTools tools,
            CancellationToken cancellationToken) => Task.FromResult(new ConnectionSchema("1 service", [], ""));
    }

    private static RpadConnection Described(string address) => new()
    {
        Name = "stock",
        Driver = "described",
        Params = new(StringComparer.Ordinal) { ["address"] = RpadValue.Of(address) },
    };

    [AvaloniaFact]
    public async Task ASchemaWrittenElsewhere_Should_RefreshTheTabThatListsItsConnection_Only()
    {
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new DescribedDriver());
        var schemas = new ConnectionSchemas(drivers, _folder,
            new ConnectionTools { GetPackageFolderAsync = (_, _, _) => Task.FromResult("") }, _ => null);
        var listed = Described("http://listed:5000");
        var other = Described("http://other:5000");
        // Already read, so the tab's own background read has nothing to do.
        schemas.Cache.Put(listed, new ConnectionSchema("1 service", [], ""));

        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));
        var intelliSense = new Mock<IIntelliSenseService>();
        var vm = new EditorTabItemViewModel(
            null, [], prefs.Object, new Mock<IEditorTabActions>().Object, RpadLanguage.CSharp, RpadKind.Statements);
        var tab = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object,
            new Mock<ILanguageHostRegistry>().Object, prefs.Object, intelliSense.Object, new Mock<IBuildCacheService>().Object,
            connectionDrivers: drivers, connectionSchemas: schemas);
        var host = new Window { Content = tab };
        host.Show();
        try
        {
            tab.State.RpadDocument = new RpadDocument { FrontMatter = new RpadFrontMatter { Connections = [listed] }, RunCode = "" };
            // The refresh the new document starts has landed before the test counts.
            await TestWait.WhileAsync(() => intelliSense.Invocations.All(i => i.Method.Name != nameof(IIntelliSenseService.NotifyTextChanged)));
            await TestWait.SettleAsync(() => intelliSense.Invocations.Count); // and any further ones it started
            intelliSense.Invocations.Clear();

            await Task.Run(() => schemas.Cache.Put(other, new ConnectionSchema("1 service", [], "")),
                TestContext.Current.CancellationToken);
            await TestWait.WhileAsync(() => false, TimeSpan.FromMilliseconds(200)); // let a wrong refresh show
            intelliSense.Verify(s => s.ApplyExecutionContext(It.IsAny<ExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);

            await Task.Run(() => schemas.Cache.Put(listed, new ConnectionSchema("2 services", [], "")),
                TestContext.Current.CancellationToken);
            await TestWait.WhileAsync(() => intelliSense.Invocations.All(i => i.Method.Name != nameof(IIntelliSenseService.ApplyExecutionContext)));
            // The text is analysed again once the new classes are in, which happens off the UI thread.
            await TestWait.WhileAsync(() => intelliSense.Invocations.All(i => i.Method.Name != nameof(IIntelliSenseService.NotifyTextChanged)));
            intelliSense.Verify(s => s.ApplyExecutionContext(It.IsAny<ExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
            intelliSense.Verify(s => s.NotifyTextChanged(It.IsAny<string>()), Times.Once);

            tab.Dispose();
            intelliSense.Invocations.Clear();
            await Task.Run(() => schemas.Cache.Put(listed, new ConnectionSchema("3 services", [], "")),
                TestContext.Current.CancellationToken);
            await TestWait.WhileAsync(() => false, TimeSpan.FromMilliseconds(200));
            Assert.DoesNotContain(intelliSense.Invocations, i => i.Method.Name == nameof(IIntelliSenseService.ApplyExecutionContext));
        }
        finally { host.Close(); }
    }
}
