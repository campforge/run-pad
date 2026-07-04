using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DotPad.Build;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using DotPad.Languages.CSharp.Sdk;
using DotPad.Logging;
using DotPad.Services;
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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _serviceProvider = ConfigureServices();

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
        services.AddSingleton<IDotpFileService, DotpFileService>();
        services.AddSingleton<ISdkProvider, DotnetSdkProvider>();
        services.AddSingleton<IPackageSearchService, NuGetPackageSearchService>();
        services.AddSingleton<ILocalPackageCacheService, LocalPackageCacheService>();
        services.AddSingleton<IWindowService, WindowService>();

        services.AddLogging(logging =>
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DotPad", "logs");
            logging.AddProvider(new FileLoggerProvider(
                Path.Combine(logDir, $"dotpad-{DateTime.Now:yyyy-MM-dd}.log")));
            logging.SetMinimumLevel(LogLevel.Debug);
        });

        services.AddSingleton<ILanguageHostRegistry>(sp =>
        {
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            LanguageHostRegistry registry = new();
            registry.Register(new CSharpLanguageHostFactory(loggerFactory));
            return registry;
        });

        services.AddSingleton<IIntelliSenseService>(sp =>
        {
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var refService = new CSharpReferenceService();
            var refs = refService.GetDefault().Append(refService.GetExtensionReference());
            return new CSharpIntelliSenseService(refs, loggerFactory.CreateLogger<CSharpIntelliSenseService>());
        });

        services.AddTransient<MainWindow>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<PreferencesWindowViewModel>();
        services.AddTransient<DependenciesWindowViewModel>();
        services.AddTransient<PreferencesWindow>();
        services.AddTransient<DependenciesWindow>();
        services.AddTransient<EditorTabsControl>();

        return services.BuildServiceProvider();
    }
}
