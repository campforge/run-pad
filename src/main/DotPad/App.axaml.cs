using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DotPad.Build;
using DotPad.Build.Core;
using DotPad.Contracts;
using DotPad.Languages.CSharp;
using DotPad.Logging;
using DotPad.Services;
using DotPad.Services.Preferences;
using DotPad.ViewModels;
using DotPad.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotPad;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = ConfigureServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = services.GetRequiredService<MainWindow>();
            desktop.MainWindow = mainWindow;
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
        services.AddSingleton<DotnetSdksService>();
        services.AddLogging(logging =>
        {
            var logDir  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DotPad", "logs");
            logging.AddProvider(new FileLoggerProvider(
                Path.Combine(logDir, $"dotpad-{DateTime.Now:yyyy-MM-dd}.log")));
            logging.SetMinimumLevel(LogLevel.Debug);
        });

        services.AddSingleton<ILanguageHostRegistry>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var registry = new LanguageHostRegistry();
            registry.Register(new CSharpLanguageHostFactory(loggerFactory));
            return registry;
        });

        services.AddSingleton<IEditorTabFactory, EditorTabFactory>();
        services.AddTransient<MainWindow>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<PreferencesWindowViewModel>();
        services.AddTransient<PreferencesWindow>();

        return services.BuildServiceProvider();
    }
}
