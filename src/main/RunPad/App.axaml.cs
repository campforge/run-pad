using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using RunPad.Connectors.Grpc;
using RunPad.Connectors.Http;
using RunPad.Connectors.Sqlite;
using RunPad.Contracts;
using RunPad.Contracts.Connections;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Sdk;
using RunPad.Languages.VB;
using RunPad.Logging;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Connections;
using RunPad.Services.Preferences;
using RunPad.Services.Recovery;
using RunPad.Services.Scaling;
using RunPad.Services.Secrets;
using RunPad.Services.Setup;
using RunPad.UserControls;
using RunPad.Windows.Models;
using ConnectionsWindow = RunPad.Windows.ConnectionsWindow;
using DependenciesWindow = RunPad.Windows.DependenciesWindow;
using MainWindow = RunPad.Windows.MainWindow;
using PreferencesWindow = RunPad.Windows.PreferencesWindow;
using SecretStoreWindow = RunPad.Windows.SecretStoreWindow;
using SetupWizardWindow = RunPad.Windows.SetupWizardWindow;
using SplashWindow = RunPad.Windows.SplashWindow;

namespace RunPad;

public class App : Application
{
    // How long a script's build-cache directory can sit unused before PruneStale reclaims it
    //.
    private static readonly TimeSpan BuildCacheMaxAge = TimeSpan.FromDays(14);

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
            // applied: every "RunPad.*" DynamicResource lookup came back empty (RunPad's palettes are
            // keyed by custom ThemeVariants like "VsCodeDark", not just "Dark"/"Light", so nothing
            // matches the still-default variant), so the window fell back to a plain black/white
            // background instead of the intended panel color. Resolving it here — before either
            // branch below shows a window — guarantees the theme is always applied first.
            serviceProvider.GetRequiredService<IThemeService>();

            // Before any window exists, so each one is first laid out at the interface scale.
            var viewScale = ViewScale.For(serviceProvider.GetRequiredService<IPreferencesService>());
            InterfaceScaling.Attach(viewScale);

            var splash = new SplashWindow();
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
            Dispatcher.UIThread.Post(() => _ = StartDesktopAsync(desktop, splash, serviceProvider, logger), DispatcherPriority.Background);
            return;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartDesktopAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash,
        ServiceProvider serviceProvider,
        ILogger logger)
    {
        try
        {
            splash.SetStartupStatus("Checking .NET SDK and debugger…");
            var prerequisiteStatus = await Task.Run(() => serviceProvider.GetRequiredService<IPrerequisiteService>().Check());

            if (prerequisiteStatus.IsComplete)
            {
                ShowStartupWindow(serviceProvider.GetRequiredService<MainWindow>(), desktop, splash);
                return;
            }

            // First-run setup wizard is shown instead of MainWindow when the .NET SDK or
            // netcoredbg debugger is missing/unconfigured. Reassign MainWindow before closing the
            // splash so ShutdownMode.OnMainWindowClose does not exit the app between windows.
            var wizard = serviceProvider.GetRequiredService<SetupWizardWindow>();
            wizard.Continued += () =>
            {
                var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
                desktop.MainWindow = mainWindow;
                CloseSplashAfterOpened(mainWindow, splash);
                mainWindow.Show();
                wizard.Close();
            };
            ShowStartupWindow(wizard, desktop, splash);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "RunPad startup failed");
            splash.ShowStartupError();
        }
    }

    private static void ShowStartupWindow(Window window, IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
    {
        CloseSplashAfterOpened(window, splash);
        desktop.MainWindow = window;
        window.Show();
    }

    private static void CloseSplashAfterOpened(Window window, SplashWindow splash) =>
        window.Opened += (_, _) => Dispatcher.UIThread.Post(() => CloseSplash(splash), DispatcherPriority.Background);

    private static void CloseSplash(SplashWindow splash)
    {
        if (splash.IsVisible) splash.Close();
    }

    private ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<Application>(this);
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<ISdkProvider, DotnetSdkProvider>();
        services.AddSingleton<IRuntimeReferenceLocator, DotnetRuntimeReferenceLocator>();
        services.AddSingleton<INuGetSourceStore, RunPadNuGetConfigStore>();
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

        services.AddTransient<MainWindow>();
        services.AddSingleton<IRecoveryStore>(_ => new DiskRecoveryStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RunPad", "recovery", "v1")));
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<PreferencesWindowViewModel>();
        services.AddTransient<DependenciesWindowViewModel>();
        services.AddTransient<PreferencesWindow>();
        services.AddTransient<DependenciesWindow>();
        services.AddSingleton<ISecretStore>(_ => new FileSecretStore());
        services.AddTransient<SecretStoreWindowViewModel>();
        services.AddTransient<SecretStoreWindow>();
        // Connection drivers are part of RunPad for now, registered here like the languages above.
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
        services.AddTransient<ConnectionsWindowViewModel>();
        services.AddTransient<ConnectionsWindow>();
        services.AddTransient<SetupWizardViewModel>();
        services.AddTransient<SetupWizardWindow>();
        services.AddSingleton<EditorTabsControl>();

        // WindowService's own Func<TWindow> factories (a fresh Transient instance
        // each time a dialog is shown).
        services.AddSingleton<Func<DependenciesWindow>>(sp => () => sp.GetRequiredService<DependenciesWindow>());
        services.AddSingleton<Func<PreferencesWindow>>(sp => () => sp.GetRequiredService<PreferencesWindow>());
        services.AddSingleton<Func<SecretStoreWindow>>(sp => () => sp.GetRequiredService<SecretStoreWindow>());
        services.AddSingleton<Func<ConnectionsWindow>>(sp => () => sp.GetRequiredService<ConnectionsWindow>());

        var provider = services.BuildServiceProvider();

        // ToDo: split this out into a dedicated app warmup service
        // Each language prepares its editor services in the background so the first tab's completion is fast.
        _ = Task.Run(() => provider.GetRequiredService<ILanguageHostRegistry>().WarmUpIntelliSense());

        // Reclaim build-cache directories for files that haven't been run in a while, so
        // %TEMP%\RunPad doesn't grow unbounded now that directories persist across restarts.
        _ = Task.Run(() => provider.GetRequiredService<IBuildCacheService>().PruneStale(BuildCacheMaxAge));

        return provider;
    }

    private static void AddLogging(ServiceCollection services)
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RunPad", "logs");
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
