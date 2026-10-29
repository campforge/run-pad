using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Invoklet.Connectors.Grpc;
using Invoklet.Connectors.Http;
using Invoklet.Connectors.Sqlite;
using Invoklet.Contracts;
using Invoklet.Contracts.Connections;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Sdk;
using Invoklet.Languages.VB;
using Invoklet.Logging;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Connections;
using Invoklet.Services.Desktop;
using Invoklet.Services.Preferences;
using Invoklet.Services.Recovery;
using Invoklet.Services.Scaling;
using Invoklet.Services.Secrets;
using Invoklet.Services.Setup;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ConnectionsWindow = Invoklet.Windows.ConnectionsWindow;
using DependenciesWindow = Invoklet.Windows.DependenciesWindow;
using MainWindow = Invoklet.Windows.MainWindow;
using PreferencesWindow = Invoklet.Windows.PreferencesWindow;
using SecretStoreWindow = Invoklet.Windows.SecretStoreWindow;
using SetupWizardWindow = Invoklet.Windows.SetupWizardWindow;
using SplashWindow = Invoklet.Windows.SplashWindow;

using Windows_ConnectionsWindow = Invoklet.Windows.ConnectionsWindow;
using Windows_DependenciesWindow = Invoklet.Windows.DependenciesWindow;
using Windows_MainWindow = Invoklet.Windows.MainWindow;
using Windows_PreferencesWindow = Invoklet.Windows.PreferencesWindow;
using Windows_SecretStoreWindow = Invoklet.Windows.SecretStoreWindow;
using Windows_SetupWizardWindow = Invoklet.Windows.SetupWizardWindow;
using Windows_SplashWindow = Invoklet.Windows.SplashWindow;

namespace Invoklet;

public class App : Application
{
    // How long a script's build-cache directory can sit unused before PruneStale reclaims it
    //.
    private static readonly TimeSpan BuildCacheMaxAge = TimeSpan.FromDays(14);

    // How long a connection's cached schema (a gRPC server's services, for instance) can sit
    // unread before PruneStale reclaims it, so %APPDATA%/Invoklet/connection-schemas doesn't grow
    // unbounded with files left behind by removed, renamed or changed connections.
    private static readonly TimeSpan ConnectionSchemaCacheMaxAge = TimeSpan.FromDays(30);

    private ServiceProvider? _serviceProvider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Avalonia.Native creates its application-menu
        // exporter in an AppBuilder.AfterSetup callback, i.e. right after this method returns and
        // before OnFrameworkInitializationCompleted. If Application has no NativeMenu by then, the
        // exporter installs its own default app menu (the "About Avalonia" item) and doesn't pick up
        // a menu assigned later. Setting it here — before any window, including SetupWizardWindow —
        // is the earliest point the Application exists. Other platforms ignore Application menus.
        NativeMenu.SetMenu(this, CreateApplicationMenu());
    }

    // Deliberately empty: every command lives in MainWindow's menu, so each OS has the same
    // File/Edit/…/Help paths (About under Help, Exit under File). Program.BuildAvaloniaApp turns off
    // the items macOS would otherwise append (Services, Hide, Show All, Quit).
    internal static NativeMenu CreateApplicationMenu() => new();

    public override void OnFrameworkInitializationCompleted()
    {
        // App-wide setup of this OS's desktop integration (macOS's Control-click, Linux's UI font, …).
        DesktopIntegration.Current.OnApplicationStarted(this);
        WindowChrome.Install(this, DesktopIntegration.Current);
        var serviceProvider = ConfigureServices();
        _serviceProvider = serviceProvider;

        ILogger logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<App>();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (IsTerminating: {IsTerminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // ThemeService is a singleton, so it's only ever constructed (and applies
            // RequestedThemeVariant) once something actually depends on IThemeService — which
            // MainWindow does via MainWindowViewModel, but SetupWizardWindow doesn't. Left implicit,
            // a first run with a missing SDK/debugger showed the wizard BEFORE the theme was ever
            // applied: every "Invoklet.*" DynamicResource lookup came back empty (Invoklet's palettes are
            // keyed by custom ThemeVariants like "VsCodeDark", not just "Dark"/"Light", so nothing
            // matches the still-default variant), so the window fell back to a plain black/white
            // background instead of the intended panel color. Resolving it here — before either
            // branch below shows a window — guarantees the theme is always applied first.
            serviceProvider.GetRequiredService<IThemeService>();

            // Before any window exists, so each one is first laid out at the interface scale.
            var viewScale = ViewScale.For(serviceProvider.GetRequiredService<IPreferencesService>());
            InterfaceScaling.Attach(viewScale);

            var splash = new Windows_SplashWindow();
            desktop.MainWindow = splash;

            ApplicationExit.Attach(() => desktop.MainWindow, serviceProvider.GetRequiredService<IPreferencesService>(), desktop);
            desktop.Exit += (_, _) =>
            {
                viewScale.Flush();
                serviceProvider.Dispose();
            };

            base.OnFrameworkInitializationCompleted();

            // Let Avalonia open and render the splash before checking installed tools or creating
            // the editor window. The prerequisite check can start a dotnet process on first launch.
            Dispatcher.UIThread.Post(() => _ = StartDesktopAsync(desktop, splash,
                () =>
                {
                    var prerequisites = serviceProvider.GetRequiredService<IPrerequisiteService>();
                    return Task.Run(() => prerequisites.Check().IsComplete);
                },
                complete => CreateStartupWindow(complete, desktop, serviceProvider, logger), logger), DispatcherPriority.Background);
            return;
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static async Task StartDesktopAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        Windows_SplashWindow splash,
        Func<Task<bool>> checkPrerequisites,
        Func<bool, Window> createWindow,
        ILogger logger)
    {
        if (splash.HasClosed) return;
        try
        {
            splash.SetStartupStatus("Checking .NET SDK and debugger…");
            var complete = await checkPrerequisites();
            if (splash.HasClosed) return;
            ShowStartupWindow(createWindow(complete), desktop);
        }
        catch (Exception ex)
        {
            if (splash.HasClosed) return;
            desktop.MainWindow = splash;
            logger.LogCritical(ex, "Invoklet startup failed");
            splash.ShowStartupError();
        }
    }

    private static Window CreateStartupWindow(bool complete,
        IClassicDesktopStyleApplicationLifetime desktop, ServiceProvider services, ILogger logger)
    {
        if (complete) return services.GetRequiredService<Windows_MainWindow>();

        var wizard = services.GetRequiredService<Windows_SetupWizardWindow>();
        wizard.Continued += () =>
        {
            try { ShowStartupWindow(services.GetRequiredService<Windows_MainWindow>(), desktop); }
            catch (Exception ex) { logger.LogError(ex, "Opening the workspace after setup failed"); }
        };
        return wizard;
    }

    internal static void ShowStartupWindow(Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var previous = desktop.MainWindow;
        desktop.MainWindow = window;
        var targetOpacity = window.Opacity;
        // Construct and lay out the destination while it remains invisible. This prevents a
        // partially initialized editor (empty tabs, unstyled controls, or placeholder panes)
        // from flashing between the splash screen and the ready workspace.
        window.Opacity = 0;
        try { window.Show(); }
        catch
        {
            window.Opacity = targetOpacity;
            desktop.MainWindow = previous;
            if (window.IsVisible) window.Close();
            throw;
        }
        _ = RevealStartupWindowAsync(window, desktop, previous, targetOpacity);
    }

    // At most this long behind the splash, so a stuck tab can't keep Invoklet from showing at all.
    internal static TimeSpan StartupReadyTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private static async Task RevealStartupWindowAsync(Window window,
        IClassicDesktopStyleApplicationLifetime desktop, Window? previous, double targetOpacity)
    {
        if (window is IStartupWindow { Ready: { IsCompleted: false } ready })
        {
            (previous as Windows_SplashWindow)?.SetStartupStatus("Opening scripts…");
            await Task.WhenAny(ready, Task.Delay(StartupReadyTimeout));
        }
        // Give the destination two frames: one for the initial layout and one for bindings and
        // deferred control initialization. Only then reveal it and close the previous window.
        Dispatcher.UIThread.Post(() =>
        {
            if (!window.IsVisible || !ReferenceEquals(desktop.MainWindow, window)) return;
            window.UpdateLayout();
            Dispatcher.UIThread.Post(() =>
            {
                if (!window.IsVisible || !ReferenceEquals(desktop.MainWindow, window)) return;
                window.Opacity = targetOpacity;
                previous?.Close();
            }, DispatcherPriority.Render);
        }, DispatcherPriority.Background);
    }

    private ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<Application>(this);
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton(DesktopIntegration.Current);
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<ISdkProvider, DotnetSdkProvider>();
        services.AddSingleton<IRuntimeReferenceLocator, DotnetRuntimeReferenceLocator>();
        services.AddSingleton<INuGetSourceStore, InvokletNuGetConfigStore>();
        services.AddSingleton<IPackageSearchService, NuGetPackageSearchService>();
        services.AddSingleton<ILocalPackageCacheService, LocalPackageCacheService>();
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<IBuildCacheService, BuildCacheService>();
        services.AddSingleton<IPrerequisiteService, PrerequisiteService>();
        services.AddSingleton<ISetupDownloader, SetupDownloader>();
        services.AddSingleton<ISetupProcessRunner, SetupProcessRunner>();
        services.AddSingleton<ISetupInstallerService, SetupInstallerService>();

        AddLogging(services);

        services.AddSingleton<ILanguageHostRegistry>(sp =>
        {
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            LanguageHostRegistry registry = new();
            var runtimeReferences = sp.GetRequiredService<IRuntimeReferenceLocator>();
            registry.Register(new CSharpLanguageHostFactory(loggerFactory, runtimeReferences));
            registry.Register(new VbLanguageHostFactory(loggerFactory, runtimeReferences));
            return registry;
        });

        services.AddTransient<Windows_MainWindow>();
        services.AddSingleton<IRecoveryStore>(_ => new DiskRecoveryStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Invoklet", "recovery", "v1")));
        services.AddSingleton<ISessionSnapshotLoader, DiskSessionSnapshotLoader>();
        services.AddTransient<ISessionRecoveryCoordinator, SessionRecoveryCoordinator>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<PreferencesWindowViewModel>();
        services.AddTransient<DependenciesWindowViewModel>();
        services.AddTransient<Windows_PreferencesWindow>();
        services.AddTransient<Windows_DependenciesWindow>();
        services.AddSingleton<ISecretStore>(_ => new FileSecretStore());
        services.AddTransient<SecretStoreWindowViewModel>();
        services.AddTransient<Windows_SecretStoreWindow>();
        // Connection drivers are part of Invoklet for now, registered here like the languages above.
        services.AddSingleton(_ =>
        {
            var registry = new ConnectionDriverRegistry();
            registry.Register(new HttpConnectionDriver());
            registry.Register(new GrpcConnectionDriver());
            registry.Register(new SqliteConnectionDriver());
            return registry;
        });
        // What some drivers read from the endpoint before a build (a gRPC server's services), kept
        // on disk; secrets for reading it come from the Secret Store.
        services.AddSingleton(sp => new ConnectionSchemas(sp.GetRequiredService<ConnectionDriverRegistry>(),
            ConnectionSchemaCache.DefaultFolder, DotnetConnectionTools.Default,
            sp.GetRequiredService<ISecretStore>().Find));
        services.AddSingleton<IConnectionStore>(_ => new FileConnectionStore());
        // Navigation rows and attached/collapsed state belong to one main-window session.
        services.AddTransient(sp => new ConnectionNavigationViewModel(
            sp.GetRequiredService<IConnectionStore>(), sp.GetRequiredService<ConnectionDriverRegistry>(),
            secrets: sp.GetRequiredService<ISecretStore>(), schemas: sp.GetRequiredService<ConnectionSchemas>()));
        services.AddTransient<ConnectionsWindowViewModel>();
        services.AddTransient<Windows_ConnectionsWindow>();
        services.AddTransient<SetupWizardViewModel>();
        services.AddTransient<Windows_SetupWizardWindow>();
        // The control owns tab controls and subscriptions; it must follow MainWindow's lifetime.
        services.AddTransient<EditorTabsControl>();

        // WindowService's own Func<TWindow> factories (a fresh Transient instance
        // each time a dialog is shown).
        services.AddSingleton<Func<Windows_DependenciesWindow>>(sp => () => sp.GetRequiredService<Windows_DependenciesWindow>());
        services.AddSingleton<Func<Windows_PreferencesWindow>>(sp => () => sp.GetRequiredService<Windows_PreferencesWindow>());
        services.AddSingleton<Func<Windows_SecretStoreWindow>>(sp => () => sp.GetRequiredService<Windows_SecretStoreWindow>());
        services.AddSingleton<Func<Windows_ConnectionsWindow>>(sp => () => sp.GetRequiredService<Windows_ConnectionsWindow>());

        var provider = services.BuildServiceProvider();

        // Before any script runs: the Secret Store's key goes to the scripts this process starts, and
        // a store still in plain text is encrypted. The keychain may ask the user to unlock it first.
        if (provider.GetRequiredService<ISecretStore>() is FileSecretStore secretStore) secretStore.PrepareForScripts();

        // ToDo: split this out into a dedicated app warmup service
        // Each language prepares its editor services in the background so the first tab's completion is fast.
        _ = Task.Run(() => provider.GetRequiredService<ILanguageHostRegistry>().WarmUpIntelliSense());

        // Reclaim build-cache directories for files that haven't been run in a while, so
        // %TEMP%\Invoklet doesn't grow unbounded now that directories persist across restarts.
        _ = Task.Run(() => provider.GetRequiredService<IBuildCacheService>().PruneStale(BuildCacheMaxAge));

        // Reclaim connection schema files left behind by connections removed, renamed or changed
        // since they were last read.
        _ = Task.Run(() => provider.GetRequiredService<ConnectionSchemas>().Cache.PruneStale(ConnectionSchemaCacheMaxAge));

        return provider;
    }

    private static void AddLogging(ServiceCollection services)
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Invoklet", "logs");
        Directory.CreateDirectory(logDir);
        services.AddLogging(lb =>
        {
            lb.AddConsole();
            lb.AddProvider(new FileLoggerProvider(Path.Combine(logDir)));
            // Debug-level logging includes DAP traffic and snippets of the user's own script code —
            // fine for a debug build, too invasive to write to disk unconditionally in a release one.
#if DEBUG
            lb.SetMinimumLevel(LogLevel.Debug);
#else
            lb.SetMinimumLevel(LogLevel.Information);
#endif
        });
    }
}
