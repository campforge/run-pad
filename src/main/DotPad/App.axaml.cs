using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DotPad.Build;
using DotPad.Build.Core;
using DotPad.Contracts;
using DotPad.Languages.CSharp;
using DotPad.Resources;
using DotPad.Services;
using DotPad.ViewModels;
using DotPad.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DotPad;

public class App : Application
{
    public override void Initialize()
    {
        Resources.MergedDictionaries.Add(new Icons());
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = ConfigureServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<Application>(this);
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IOptionsService, OptionsService>();
        services.AddSingleton<IDotpFileService, DotpFileService>();
        services.AddSingleton<DotnetSdksService>();
        services.AddSingleton<ILanguageHostRegistry>(_ =>
        {
            var registry = new LanguageHostRegistry();
            registry.Register(new CSharpLanguageHostFactory());
            return registry;
        });

        services.AddTransient<MainWindow>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<PreferencesWindowViewModel>();
        services.AddTransient<PreferencesWindow>();

        return services.BuildServiceProvider();
    }
}
