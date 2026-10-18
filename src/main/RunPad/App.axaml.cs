using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using RunPad.Contracts;
using RunPad.Contracts.IntelliSense;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Sdk;
using RunPad.Languages.VB;
using RunPad.Logging;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Preferences;
using RunPad.Services.Scaling;
using RunPad.Services.Setup;
using RunPad.UserControls;
using RunPad.Windows.Models;
using DependenciesWindow = RunPad.Windows.DependenciesWindow;
using MainWindow = RunPad.Windows.MainWindow;
using PreferencesWindow = RunPad.Windows.PreferencesWindow;
using SetupWizardWindow = RunPad.Windows.SetupWizardWindow;

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
        _serviceProvider = ConfigureServices();

        ILogger logger = _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<App>();
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
            _serviceProvider.GetRequiredService<IThemeService>();

            // Before any window exists, so each one is first laid out at the interface scale.
            var viewScale = ViewScale.For(_serviceProvider.GetRequiredService<IPreferencesService>());
            InterfaceScaling.Attach(viewScale);

            var prerequisites = _serviceProvider.GetRequiredService<IPrerequisiteService>();
            if (prerequisites.Check().IsComplete)
            {
                desktop.MainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            }
            else
            {
                // First-run setup wizard (live report 2026-07-25) — shown instead of
                // MainWindow when the .NET SDK or netcoredbg debugger is missing/unconfigured, so a
                // fresh install gets an actionable "install this for me" instead of Run/Debug just
                // silently failing. Reassigning desktop.MainWindow to the real MainWindow BEFORE
                // closing the wizard (rather than after) keeps ShutdownMode.OnMainWindowClose from
                // tearing the whole app down when the wizard window itself closes.
                var wizard = _serviceProvider.GetRequiredService<SetupWizardWindow>();
                wizard.Continued += () =>
                {
                    var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    wizard.Close();
                };
                desktop.MainWindow = wizard;
            }

            ApplicationExit.Attach(() => desktop.MainWindow, _serviceProvider.GetRequiredService<IPreferencesService>(), desktop);
            desktop.Exit += (_, _) =>
            {
                viewScale.Flush();
                _serviceProvider.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
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
            registry.Register(new CSharpLanguageHostFactory(loggerFactory));
            registry.Register(new VbLanguageHostFactory(loggerFactory));
            return registry;
        });

        // ToDo: some stuff here that should move directly into the language implementation
        services.AddSingleton<CSharpReferenceService>();
        // One IIntelliSenseService instance per tab, not a shared singleton: each open script can
        // reference different NuGet packages, and CSharpIntelliSenseService is stateful (one
        // AdhocWorkspace/document per instance). A shared instance would let one tab's package
        // references and last-synced text stomp another tab's — see EditorTabsControl.CreateControl.
        services.AddSingleton<Func<IIntelliSenseService>>(sp => () =>
        {
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var refService = sp.GetRequiredService<CSharpReferenceService>();
            var runtime = sp.GetRequiredService<IRuntimeReferenceLocator>();
            return new CSharpIntelliSenseService(refService.GetDefault(), loggerFactory.CreateLogger<CSharpIntelliSenseService>(),
                additionalReferencePaths: [runtime.GetRuntimeAssemblyPath()]);
        });

        services.AddTransient<MainWindow>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<PreferencesWindowViewModel>();
        services.AddTransient<DependenciesWindowViewModel>();
        services.AddTransient<PreferencesWindow>();
        services.AddTransient<DependenciesWindow>();
        services.AddTransient<SetupWizardViewModel>();
        services.AddTransient<SetupWizardWindow>();
        services.AddSingleton<EditorTabsControl>();

        // WindowService's own Func<TWindow> factories (a fresh Transient instance
        // each time a dialog is shown, same reasoning as Func<IIntelliSenseService> above).
        services.AddSingleton<Func<DependenciesWindow>>(sp => () => sp.GetRequiredService<DependenciesWindow>());
        services.AddSingleton<Func<PreferencesWindow>>(sp => () => sp.GetRequiredService<PreferencesWindow>());

        var provider = services.BuildServiceProvider();

        // ToDo: split this out into a dedicated app warmup service
        // Kick off Roslyn warmup in the background so the first tab's completion is fast.
        // Uses a throwaway instance — the expensive part (RunPadRoslynHost.HostServices / MEF
        // composition) is a process-wide lazy singleton shared by every instance the factory creates.
        _ = Task.Run(() => provider.GetRequiredService<Func<IIntelliSenseService>>()().NotifyTextChanged(""));

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
