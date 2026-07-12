using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using DotPad.Languages.CSharp.Sdk;
using DotPad.Languages.VB;
using DotPad.Logging;
using DotPad.Services;
using DotPad.Services.BuildCache;
using DotPad.Services.Preferences;
using DotPad.UserControls;
using DotPad.Windows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DependenciesWindow = DotPad.Windows.DependenciesWindow;
using MainWindow = DotPad.Windows.MainWindow;
using PreferencesWindow = DotPad.Windows.PreferencesWindow;

namespace DotPad;

public class App : Application
{
    private ServiceProvider? _serviceProvider;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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
            desktop.MainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            desktop.Exit += (_, _) => _serviceProvider.Dispose();
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
        services.AddSingleton<IPackageSearchService, NuGetPackageSearchService>();
        services.AddSingleton<ILocalPackageCacheService, LocalPackageCacheService>();
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<IBuildCacheService, BuildCacheService>();

        AddLogging(services);

        services.AddSingleton<ILanguageHostRegistry>(sp =>
        {
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            LanguageHostRegistry registry = new();
            registry.Register(new CSharpLanguageHostFactory(loggerFactory));
            registry.Register(new VbLanguageHostFactory(loggerFactory));
            return registry;
        });

        // ToDo: Какаято фигня которая должна переехать непосредственно в мплементацию языка
        services.AddSingleton<CSharpReferenceService>();
        // One IIntelliSenseService instance per tab, not a shared singleton: each open script can
        // reference different NuGet packages, and CSharpIntelliSenseService is stateful (one
        // AdhocWorkspace/document per instance). A shared instance would let one tab's package
        // references and last-synced text stomp another tab's — see EditorTabsControl.CreateControl.
        services.AddSingleton<Func<IIntelliSenseService>>(sp => () =>
        {
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var refService = sp.GetRequiredService<CSharpReferenceService>();
            var refs = refService.GetDefault().Append(refService.GetExtensionReference());
            return new CSharpIntelliSenseService(refs, loggerFactory.CreateLogger<CSharpIntelliSenseService>());
        });

        services.AddTransient<MainWindow>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<PreferencesWindowViewModel>();
        services.AddTransient<DependenciesWindowViewModel>();
        services.AddTransient<PreferencesWindow>();
        services.AddTransient<DependenciesWindow>();
        services.AddSingleton<EditorTabsControl>();

        var provider = services.BuildServiceProvider();

        // ToDo: выделить отдельный сервис прогрева приложения
        // Kick off Roslyn warmup in the background so the first tab's completion is fast.
        // Uses a throwaway instance — the expensive part (DotPadRoslynHost.HostServices / MEF
        // composition) is a process-wide lazy singleton shared by every instance the factory creates.
        _ = Task.Run(() => provider.GetRequiredService<Func<IIntelliSenseService>>()().NotifyTextChanged(""));

        // Reclaim build-cache directories for files that haven't been run in a while, so
        // %TEMP%\DotPad doesn't grow unbounded now that directories persist across restarts.
        _ = Task.Run(() => provider.GetRequiredService<IBuildCacheService>().PruneStale(TimeSpan.FromDays(14)));

        return provider;
    }

    private static void AddLogging(ServiceCollection services)
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DotPad", "logs");
        Directory.CreateDirectory(logDir);
        services.AddLogging(lb =>
        {
            lb.AddConsole();
            lb.AddProvider(new FileLoggerProvider(Path.Combine(logDir)));
            lb.SetMinimumLevel(LogLevel.Debug);
        });
    }
}
