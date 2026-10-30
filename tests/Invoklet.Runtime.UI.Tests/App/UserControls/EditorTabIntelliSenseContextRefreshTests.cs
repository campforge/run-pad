using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.Connections;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Runtime.UI.Tests.Infrastructure;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.UserControls;

// A tab with gRPC-like connections rebuilds the editor's context (the connections' classes, the
// references) when it opens, after a build and when a schema is read elsewhere. That work can be slow
// for a large contract, so it happens off the UI thread, and only the newest request may be applied.
[Trait("Category", "Integration")]
public sealed class EditorTabIntelliSenseContextRefreshTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("Invoklet-tab-context-refresh").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    private sealed class DescribedDriver : IConnectionSchemaDriver
    {
        public string Id => "described";
        public string DisplayName => "Described";
        public ConnectionCategory Category => ConnectionCategory.Grpc;
        public IReadOnlyList<ConnectionParam> Params { get; } = [];
        public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string>();
        public IReadOnlyList<string> Validate(MdRunConnection connection) => [];
        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(true, ""));
        public ConnectionClientCode CreateClient(MdRunConnection connection, ConnectionCodeContext context) =>
            new($"public sealed class {context.ClassName} {{ }}");
        public Task<ConnectionSchema> FetchSchemaAsync(IReadOnlyDictionary<string, MdRunValue> resolvedParams, ConnectionTools tools,
            CancellationToken cancellationToken) => Task.FromResult(new ConnectionSchema("1 service", [], ""));
    }

    private static readonly MdRunConnection Stock = new()
    {
        Name = "stock",
        Driver = "described",
        Params = new(StringComparer.Ordinal) { ["address"] = MdRunValue.Of("http://listed:5000") },
    };

    private static ConnectionSchema Version(int number) =>
        new($"{number} services", [new GeneratedSourceFile($"V{number}.cs", $"public class V{number} {{ }}")], "");

    // The IntelliSense service as a real one behaves towards its caller: the slow part (the gate)
    // is followed by a check of the token, and only a request still wanted is applied.
    private sealed class SlowService
    {
        public readonly Mock<IIntelliSenseService> Mock = new();
        public readonly ManualResetEventSlim Gate = new(true);
        public readonly ConcurrentQueue<CancellationToken> Tokens = new();
        public readonly ConcurrentQueue<ExecutionRequest> Applied = new();
        public TaskCompletionSource<ExecutionRequest> AppliedSignal { get; private set; } = NewSignal<ExecutionRequest>();
        public TaskCompletionSource<bool> CallbackFinished { get; private set; } = NewSignal<bool>();
        public TaskCompletionSource<bool> TextChangedSignal { get; private set; } = NewSignal<bool>();
        private int _entered;

        private static TaskCompletionSource<T> NewSignal<T>() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Entered => Volatile.Read(ref _entered);

        public void Clear()
        {
            Applied.Clear();
            Tokens.Clear();
            Volatile.Write(ref _entered, 0);
            Mock.Invocations.Clear();
            AppliedSignal = NewSignal<ExecutionRequest>();
            CallbackFinished = NewSignal<bool>();
            TextChangedSignal = NewSignal<bool>();
        }

        public SlowService()
        {
            // The overload without a token is the one a UI-thread caller would use: it blocks too.
            Mock.Setup(s => s.UpdateExecutionContext(It.IsAny<ExecutionRequest>()))
                .Callback<ExecutionRequest>(_ => Gate.Wait(TimeSpan.FromSeconds(10)));
            Mock.Setup(s => s.ApplyExecutionContext(It.IsAny<ExecutionRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ExecutionRequest, CancellationToken>((request, token) =>
                {
                    Tokens.Enqueue(token);
                    Interlocked.Increment(ref _entered);
                    try
                    {
                        Gate.Wait(TimeSpan.FromSeconds(10));
                        token.ThrowIfCancellationRequested();
                        Applied.Enqueue(request);
                        AppliedSignal.TrySetResult(request);
                    }
                    finally { CallbackFinished.TrySetResult(true); }
                });
            Mock.Setup(s => s.NotifyTextChanged(It.IsAny<string>()))
                .Callback(() => TextChangedSignal.TrySetResult(true));
        }
    }

    private async Task<T> WithTabAsync<T>(SlowService service, Func<EditorsTabItemControl, ConnectionSchemas, Task<T>> body)
    {
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new DescribedDriver());
        var schemas = new ConnectionSchemas(drivers, _folder,
            new ConnectionTools { GetPackageFolderAsync = (_, _, _) => Task.FromResult("") }, _ => null);
        schemas.Cache.Put(Stock, Version(1));

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
        var vm = new EditorTabItemViewModel(
            null, [], prefs.Object, new Mock<IEditorTabActions>().Object, MdRunLanguage.CSharp, MdRunKind.Statements);
        var tab = new EditorsTabItemControl(vm, theme.Object, new Mock<IWindowService>().Object,
            new Mock<ILanguageHostRegistry>().Object, prefs.Object, service.Mock.Object, new Mock<IBuildCacheService>().Object,
            connectionDrivers: drivers, connectionSchemas: schemas);
        var host = new Window { Content = tab };
        host.Show();
        try
        {
            tab.State.MdRunDocument = new MdRunDocument { FrontMatter = new MdRunFrontMatter { Connections = [Stock] }, RunCode = "" };
            _ = await service.AppliedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await TestWait.WhileAsync(() => service.Mock.Invocations.All(i => i.Method.Name != nameof(IIntelliSenseService.NotifyTextChanged)));
            await service.CallbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await service.TextChangedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            // Both positive signals have fired; drain the queued UI callback before resetting the
            // mock, so a late initial NotifyTextChanged cannot be mistaken for the body operation.
            Dispatcher.UIThread.RunJobs();
            service.Clear();
            return await body(tab, schemas);
        }
        finally
        {
            service.Gate.Set();
            tab.Dispose();
            host.Close();
        }
    }

    private static string Source(ExecutionRequest request) => string.Join('\n', request.Connections!.Files.Select(f => f.Name));

    [AvaloniaFact]
    public async Task ASlowContextUpdate_Should_NotBlockTheUiThread()
    {
        var service = new SlowService();
        await WithTabAsync(service, async (tab, schemas) =>
        {
            service.Gate.Reset();
            var watch = Stopwatch.StartNew();
            tab.RefreshIntelliSensePackages();
            var blocked = watch.Elapsed;
            Assert.True(blocked < TimeSpan.FromSeconds(1), $"the UI thread waited {blocked.TotalSeconds:0.0}s for the context update");

            service.Gate.Set();
            await TestWait.WhileAsync(() => service.Applied.IsEmpty);
            Assert.Single(service.Applied);
            return 0;
        });
    }

    [AvaloniaFact]
    public async Task ARefreshOvertakenByANewerOne_Should_BeDropped_AndTheNewestApplied()
    {
        var service = new SlowService();
        await WithTabAsync(service, async (tab, schemas) =>
        {
            service.Gate.Reset();
            tab.RefreshIntelliSensePackages();
            await TestWait.WhileAsync(() => service.Entered < 1);

            // The schema changes while the first update is still working: the second sees V2.
            await Task.Run(() => schemas.Cache.Put(Stock, Version(2)), TestContext.Current.CancellationToken);
            await TestWait.WhileAsync(() => service.Entered < 2);
            Assert.True(service.Tokens.First().IsCancellationRequested, "the older update was not cancelled");

            service.Gate.Set(); // the slow older one finishes last
            var appliedRequest = await service.AppliedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.EndsWith(".V2.cs", Source(appliedRequest));
            var applied = Assert.Single(service.Applied);
            Assert.EndsWith(".V2.cs", Source(applied));
            return 0;
        });
    }

    [AvaloniaFact]
    public async Task AFewSchemaChangesAtOnce_Should_ApplyTheLastOne_AndAnalyseTheTextOnce()
    {
        var service = new SlowService();
        await WithTabAsync(service, async (tab, schemas) =>
        {
            service.Gate.Reset();
            await Task.Run(() =>
            {
                schemas.Cache.Put(Stock, Version(2));
                schemas.Cache.Put(Stock, Version(3));
                schemas.Cache.Put(Stock, Version(4));
            }, TestContext.Current.CancellationToken);
            // Overtaken updates are cancelled and merged, so the number of entries is not fixed:
            // wait until they stop arriving.
            await TestWait.SettleAsync(() => service.Entered);
            service.Gate.Set();

            await TestWait.WhileAsync(() => service.Applied.IsEmpty);
            await TestWait.WhileAsync(() => service.Mock.Invocations.All(i =>
                i.Method.Name != nameof(IIntelliSenseService.NotifyTextChanged)));
            var applied = Assert.Single(service.Applied);
            Assert.EndsWith(".V4.cs", Source(applied));
            service.Mock.Verify(s => s.NotifyTextChanged(It.IsAny<string>()), Times.Once);
            return 0;
        });
    }

    [AvaloniaFact]
    public async Task ClosingTheTab_Should_DropTheUpdateStillRunning()
    {
        var service = new SlowService();
        await WithTabAsync(service, async (tab, schemas) =>
        {
            service.Gate.Reset();
            tab.RefreshIntelliSensePackages();
            await TestWait.WhileAsync(() => service.Entered < 1);

            tab.Dispose();
            service.Gate.Set();
            await service.CallbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.True(service.Tokens.Single().IsCancellationRequested);
            Assert.Empty(service.Applied);
            service.Mock.Verify(s => s.NotifyTextChanged(It.IsAny<string>()), Times.Never);
            return 0;
        });
    }
}
