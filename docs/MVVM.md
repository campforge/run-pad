В Avalonia 12 использование встроенного контейнера Microsoft.Extensions.DependencyInjection (MS.DI) — это самый надежный выбор. В этой версии разработчики Avalonia убрали классический reflection-based ViewLocator из стандартных шаблонов, так как он мешает Native AOT компиляции и обрезке (Trimming). [1] 
Ниже представлена детальная архитектурная инструкция по организации MVVM + DI в Avalonia 12 и разбор всех критических и «болевых» точек.
------------------------------
## Архитектурный паттерн: Сквозное разрешение (Zero-Locator)
Лучшая практика для Avalonia 12 — не использовать Service Locator во View или ViewLocator. Вместо этого вся иерархия интерфейса строится через композицию самих ViewModels (внедрение дочерних VM в родительские), а сопоставление с View происходит через строго типизированные шаблоны данных (DataTemplate) в XAML. [1, 2, 3, 4, 5] 
## Шаг 1: Конфигурация IoC в App.axaml.cs
Мы инициализируем контейнер и строим дерево зависимостей. Обратите внимание: MainWindow или MainView запрашиваются из DI, а не создаются через new. [6] 

using System;using Avalonia;using Avalonia.Controls.ApplicationLifetimes;using Avalonia.Markup.Xaml;using Microsoft.Extensions.DependencyInjection;
namespace Avalonia12DI;
public partial class App : Application
{
    // Храним провайдер только для корневой инициализации или ленивых фабрик
    public static IServiceProvider ServiceProvider { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);

        ServiceProvider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Запрашиваем окно из DI, оно автоматически подтянет нужную ViewModel
            desktop.MainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = ServiceProvider.GetRequiredService<MainView>();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // 1. Сервисы (Бизнес-логика)
        services.AddSingleton<IApiService, ApiService>();

        // 2. ViewModels (Как Transient, чтобы не копить старое состояние при перезапусках)
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();

        // 3. Views
        services.AddTransient<MainWindow>();
        services.AddTransient<MainView>();
    }
}

## Шаг 2: Связывание View и ViewModel в Code-Behind
Конструктор View принимает ViewModel как аргумент. Также обязательно оставляем пустой конструктор — без него сломается Previewer (XAML-превью) в вашей IDE! [6, 7] 

using Avalonia.Controls;
namespace Avalonia12DI;
public partial class MainWindow : Window
{
    // Вызывается DI-контейнером в Runtime
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    // Вызывается Avalonia XAML Previewer в режиме дизайна
    public MainWindow()
    {
        InitializeComponent();
    }
}

------------------------------
## Разбор спорных моментов и «болевых точек»## 1. Как решать проблему с XAML Previewer?
Если View ожидает ViewModel в конструкторе, то XAML-превьюер IDE падает, так как он использует пустой конструктор и DataContext остается null. [7] 

* Решение: Используйте Design.DataContext в XAML. Это подставит фейковые данные исключительно для дизайнера, не мешая DI в рантайме.

<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://microsoft.com"
        xmlns:vm="using:Avalonia12DI.ViewModels"
        x:Class="Avalonia12DI.MainWindow"
        x:DataType="vm:MainWindowViewModel">
    
    <!-- Этот блок видит ТОЛЬКО дизайнер во время разработки -->
    <Design.DataContext>
        <vm:MainWindowViewModel />
    </Design.DataContext>

    <TextBlock Text="{Binding WelcomeText}" />
</Window>

## 2. Как открывать новые Окна (Windows) или Диалоги из ViewModel?
ViewModel не должна создавать экземпляр new MyWindow() напрямую — это нарушает абстракцию UI и ломает юнит-тестирование. [7] 

* Решение: Паттерн «Сервис Окон» (IWindowService). Мы регистрируем его как Singleton, передавая фабрику для создания окон.

public interface IWindowService
{
    void ShowSettings();
}
public class WindowService : IWindowService
{
    private readonly IServiceProvider _serviceProvider;

    public WindowService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void ShowSettings()
    {
        // Безопасно вытаскиваем Окно из DI-контейнера
        var settingsWindow = _serviceProvider.GetRequiredService<SettingsWindow>();
        settingsWindow.Show();
    }
}

Теперь в любую ViewModel достаточно внедрить IWindowService через конструктор и вызывать метод ShowSettings().
## 3. Навигация: Как менять View/Страницы на экране?
Вместо кастомных локаторов в Avalonia 12 лучше использовать строго сгенерированные DataTemplate в App.axaml.
Если родительская ViewModel хранит текущую дочернюю ViewModel в свойстве CurrentPage, Avalonia сама поймет, какое View отрисовать. [2] 

* В XAML (App.axaml):

<Application.DataTemplates>
    <!-- Если DataContext это MainViewModel, отрендерить MainView -->
    <DataTemplate DataType="using:Avalonia12DI.ViewModels.MainViewModel">
        <views:MainView />
    </DataTemplate>
    
    <!-- Если DataContext это SettingsViewModel, отрендерить SettingsView -->
    <DataTemplate DataType="using:Avalonia12DI.ViewModels.SettingsViewModel">
        <views:SettingsView />
    </DataTemplate>
</Application.DataTemplates>


* Во ViewModel:

public partial class MainNavigationViewModel : ObservableObject
{
    [ObservableProperty]
    private object _currentPage; // Сюда подставляем нужную ViewModel

    public MainNavigationViewModel(MainViewModel mainPage)
    {
        _currentPage = mainPage; // Изначально открыта главная
    }

    public void GoToSettings(SettingsViewModel settingsPage)
    {
        CurrentPage = settingsPage; // Avalonia автоматически сменит View на экране!
    }
}

## 4. Динамические параметры в ViewModel (Паттерн Фабрика)
Что делать, если UserDetailViewModel требует ID пользователя (int userId), который становится известен только во время работы программы, но при этом ей также нужен синглтон IApiService из DI?

* Решение: Использование фабричного делегата или явного класса-фабрики. Microsoft.Extensions.DependencyInjection не поддерживает фабрики "из коробки" с передачей параметров выполнения напрямую, поэтому пишется кастомная фабрика:

public class UserDetailViewModel : ObservableObject
{
    private readonly IApiService _apiService;
    public int UserId { get; }

    // Конструктор принимает и сервисы, и runtime-параметр
    public UserDetailViewModel(IApiService apiService, int userId)
    {
        _apiService = apiService;
        UserId = userId;
    }
}
// Фабрика, управляющая созданиемpublic interface IUserDetailFactory
{
    UserDetailViewModel Create(int userId);
}
public class UserDetailFactory : IUserDetailFactory
{
    private readonly IServiceProvider _serviceProvider;

    public UserDetailFactory(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

    public UserDetailViewModel Create(int userId)
    {
        // ActivatorUtilities автоматически возьмет IApiService из контейнера, 
        // а userId подставит вручную в конструктор!
        return ActivatorUtilities.CreateInstance<UserDetailViewModel>(_serviceProvider, userId);
    }
}

Регистрация в DI: services.AddSingleton<IUserDetailFactory, UserDetailFactory>();. [7] 
------------------------------
## Чек-лист жизненных циклов (Lifetimes) для Avalonia
При регистрации в MS.DI важно строго соблюдать правила, чтобы избежать утечек памяти (Memory Leaks):

* AddSingleton: Ваши инфраструктурные сервисы (ApiService, DatabaseConfiguration, WindowService). Они живут всё время работы приложения. [7] 
* AddTransient: Все ViewModels и Views. Когда пользователь закрывает страницу/окно, эти объекты должны уничтожаться сборщиком мусора (GC). Если сделать их синглтонами, старые данные будут висеть в памяти вечно. [7] 
* AddScoped: Не использовать в десктопных приложениях Avalonia. Область видимости (Scope) в десктопе не имеет четких границ (в отличие от HTTP-запросов в веб), её использование приводит к багам и путанице.

Если вы хотите развить эту тему, дайте знать:

* Планируете ли вы использовать Native AOT компиляцию?
* Нужен ли пример интеграции с ReactiveUI (поскольку вы используете Avalonia 12, синтаксис имеет свои особенности)?
* Какая архитектура у вашего приложения: одностраничная (SingleView) или многооконная (Desktop)?


[1] [https://github.com](https://github.com/AvaloniaUI/avalonia-docs/issues/525)
[2] [https://docs.avaloniaui.net](https://docs.avaloniaui.net/docs/data-templates/view-locator)
[3] [https://github.com](https://github.com/AvaloniaUI/Avalonia/discussions/15630)
[4] [https://github.com](https://github.com/AvaloniaUI/Avalonia/discussions/17576)
[5] [https://v11.docs.avaloniaui.net](https://v11.docs.avaloniaui.net/docs/concepts/view-locator)
[6] [https://github.com](https://github.com/AvaloniaUI/Avalonia/discussions/18039)
[7] [https://docs.avaloniaui.net](https://docs.avaloniaui.net/docs/app-development/dependency-injection)
