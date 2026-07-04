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


Использование Native AOT и ReactiveUI в Avalonia 12 накладывает строжайшие ограничения на архитектуру. Native AOT полностью запрещает динамическую генерацию кода (Reflection.Emit), которая раньше использовалась в ReactiveUI для связывания.
Поэтому в Avalonia 12 главным правилом становится Compile-Time Safety (компиляционная безопасность). Мы полностью отказываемся от рефлексии в пользу Source Generators (генераторов исходного кода) и строго типизированного XAML.
Ниже приведено исчерпывающее руководство по организации кода для вашего сценария с множеством окон и UserControl.
------------------------------
## Архитектурный манифест для Native AOT

   1. Никакой рефлексии при поиске View: Старый ViewLocator, который искал View по имени класса через строки, несовместим с AOT. Вместо него используется строго типизированный DataTemplate в XAML.
   2. CompiledBindings — строго обязательны: В каждом .axaml файле корневой элемент должен содержать x:CompileBindings="True". Без этого сборка под Native AOT либо упадет, либо приложение завершится ошибкой в рантайме.
   3. Source Generators вместо Reflection: Для свойств используем атрибуты [Reactive] из пакета ReactiveUI.Fody? Нет! Fody меняет IL-код и часто конфликтует с AOT. В современных реалиях для ReactiveUI и AOT используется генератор кода из CommunityToolkit.Mvvm (атрибут [ObservableProperty]), либо свойства пишутся вручную по канонам ReactiveUI, либо используется официальный Source Generator от ReactiveUI (зависит от выбранных пакетов). Ниже показан самый безопасный для AOT ручной/генераторный подход.

------------------------------
## 1. Как оформлять ViewModel (в стиле ReactiveUI + AOT)
Чтобы свойства гарантированно работали в Native AOT, пишем их через генератор или стандартный RaiseAndSetIfChanged. Использование команд через ReactiveCommand полностью безопасно.

using System;using System.Reactive;using ReactiveUI;
namespace AvaloniaAotApp.ViewModels;
// Все ViewModel регистрируем в DI как Transient!public class MainViewModel : ReactiveObject
{
    private string _title = "Главная страница";
    private readonly ITransientFactory _factory;

    // 1. Свойства пишем классическим для ReactiveUI способом (безопасно для AOT)
    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    // Дочерние ViewModel, которые будут отображаться как UserControl
    private ReactiveObject? _currentContent;
    public ReactiveObject? CurrentContent
    {
        get => _currentContent;
        set => this.RaiseAndSetIfChanged(ref _currentContent, value);
    }

    // 2. Команды ReactiveUI
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }

    // 3. Конструктор принимает зависимости из DI
    public MainViewModel(IWindowService windowService, ITransientFactory factory)
    {
        _factory = factory;
        
        // Переключение на дочерний UserControl (создается через фабрику)
        CurrentContent = _factory.Create<DashboardViewModel>();

        // Команда открытия нового окна через сервис окон
        OpenSettingsCommand = ReactiveCommand.Create(() => 
        {
            windowService.ShowSettingsWindow();
        });
    }
}

------------------------------
## 2. Как оформлять View (.axaml и .axaml.cs)
В Avalonia 12 + AOT используется концепция Constructor Injection для View. Но чтобы не сломать Previewer (дизайнер), мы делаем специальный трюк.
## Оформление .axaml (на примере UserControl):
Обратите внимание на x:CompileBindings="True" и x:DataType.

<UserControl xmlns="https://github.com"
             xmlns:x="http://microsoft.com"
             xmlns:vm="using:AvaloniaAotApp.ViewModels"
             x:Class="AvaloniaAotApp.Views.DashboardView"
             x:DataType="vm:DashboardViewModel"
             x:CompileBindings="True">
	
    <!-- Превьюер подхватит этот DataContext во время разработки -->
    <Design.DataContext>
        <!-- Для AOT тут должен быть пустой конструктор во ViewModel или mock -->
        <vm:DashboardViewModel />
    </Design.DataContext>

    <StackPanel Spacing="10" Margin="20">
        <TextBlock Text="Это дочерний UserControl (Dashboard)" Classes="h1"/>
        <TextBlock Text="{Binding StatusText}" />
    </StackPanel>
</UserControl>

## Оформление .axaml.cs (Code-Behind):
Используем Strongly Typed DataContext.

using Avalonia.Controls;using AvaloniaAotApp.ViewModels;
namespace AvaloniaAotApp.Views;
public partial class DashboardView : UserControl
{
    // Этот конструктор вызовет DI-контейнер в рантайме
    public DashboardView(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    // Этот конструктор ОСТАЕТСЯ ПУСТЫМ для Avalonia XAML Previewer
    public DashboardView()
    {
        InitializeComponent();
    }
}

------------------------------
## 3. Решение проблемы «Много Окон и UserControls» через DI
Поскольку рефлексивный ViewLocator забанен компилятором AOT, мы используем два механизма связывания.
## Механизм А: Для динамических UserControls внутри одного окна
Используем строго типизированные DataTemplate в App.axaml. Когда Avalonia видит, что во ViewModel свойство CurrentContent изменилось на DashboardViewModel, она не ищет класс по строке, а жестко скомпилировано подставляет DashboardView из DI.
В App.axaml:

<Application.DataTemplates>
    <!-- Прямое, жесткое маппинг-связывание без рефлексии -->
    <DataTemplate DataType="using:AvaloniaAotApp.ViewModels.DashboardViewModel">
        <!-- Важно: чтобы View создавалось из DI, нам нужен фабричный метод в XAML? 
             Нет, XAML создаст пустой конструктор View, НО мы переопределим это, 
             инициализируя дочерние элементы через Tree. Лучший способ для AOT ниже: -->
    </DataTemplate>
</Application.DataTemplates>

Стоп, если XAML вызывает пустой конструктор View, то View не получит ViewModel через конструктор DI!
Как решить это правильно для AOT и DI:
Вместо глобальных DataTemplate, которые вызывают пустой конструктор, внедряйте дочерние View явно через фабрику в родительское View, либо используйте контейнер ContentControl со следующим подходом:

// В родительском View (например, MainView.axaml.cs)public partial class MainView : UserControl
{
    private readonly ITransientFactory _factory;

    public MainView(MainViewModel viewModel, ITransientFactory factory)
    {
        InitializeComponent();
        DataContext = viewModel;
        _factory = factory;

        // Следим за изменением CurrentContent во ViewModel (в стиле ReactiveUI)
        viewModel.WhenAnyValue(x => x.CurrentContent)
                 .Subscribe(vm => 
                 {
                     if (vm is DashboardViewModel dashboardVm)
                     {
                         // Запрашиваем View из DI, передавая готовую VM
                         var view = _factory.Create<DashboardView>(dashboardVm);
                         ContentControlElement.Content = view;
                     }
                 });
    }

    public MainView() => InitializeComponent();
}

## Механизм Б: Для открытия новых Window
Создаем интерфейс IWindowService и фабрику ITransientFactory. Они регистрируются как Singleton.

public interface IWindowService
{
    void ShowSettingsWindow();
}
public class WindowService : IWindowService
{
    private readonly IServiceProvider _serviceProvider;

    public WindowService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void ShowSettingsWindow()
    {
        // Извлекаем Окно из DI. Контейнер сам создаст SettingsWindow, 
        // внедрит в него SettingsViewModel, а в неё — нужные сервисы.
        var window = _serviceProvider.GetRequiredService<SettingsWindow>();
        window.Show();
    }
}

Класс вспомогательной фабрики для динамического разрешения (замена ActivatorUtilities для улучшения поддержки AOT):

public interface ITransientFactory
{
    T Create<T>() where T : notnull;
    T ViewWithVm<T>(object viewModel) where T : notnull;
}
public class TransientFactory : ITransientFactory
{
    private readonly IServiceProvider _serviceProvider;
    public TransientFactory(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

    public T Create<T>() where T : notnull => _serviceProvider.GetRequiredService<T>();

    public T ViewWithVm<T>(object viewModel) where T : notnull
    {
        // Если нужно вручную прокинуть VM во View
        return (T)Activator.CreateInstance(typeof(T), viewModel)!;
    }
}

Регистрация в App.axaml.cs:

private void ConfigureServices(IServiceCollection services)
{
    services.AddSingleton<IWindowService, WindowService>();
    services.AddSingleton<ITransientFactory, TransientFactory>();

    // Окна
    services.AddTransient<MainWindow>();
    services.AddTransient<SettingsWindow>();

    // Элементы управления (UserControls)
    services.AddTransient<DashboardView>();
    services.AddTransient<ProfileView>();

    // ViewModels
    services.AddTransient<MainWindowViewModel>();
    services.AddTransient<SettingsViewModel>();
    services.AddTransient<DashboardViewModel>();
    services.AddTransient<ProfileViewModel>();
}

------------------------------
## 4. Принципы выделения ViewModel и разделения на UserControls
Десктопные приложения на Avalonia легко превратить в "спагетти", если вовремя не дробить интерфейс. Придерживайтесь следующих правил:
## По какому принципу выделять отдельную ViewModel?

   1. Принцип единственной ответственности (SRP): Если экран решает две разные задачи (например, список пользователей и детальное редактирование выбранного пользователя), это две разные ViewModel (UserListViewModel и UserEditViewModel).
   2. Переиспользуемость контекста: Если блок данных (например, карточка товара или виджет погоды) появляется в разных частях приложения, он обязан иметь свою изолированную WidgetViewModel.
   3. Жизненный цикл данных: Если при закрытии определенной вкладки или панели данные должны полностью очищаться / сбрасываться, выделите эту панель в отдельную Transient ViewModel. При уничтожении View уничтожится и VM.

## Когда нужно разделять единый AXAML на UserControls?

   1. Правило 300 строк: Если ваш .axaml файл превышает 300-400 строк разметки — его физически тяжело читать. Выносите логические блоки (боковое меню, шапку, таблицу данных) в отдельные UserControl.
   2. Сложные состояния отображения (Визуальная изоляция): Если внутри одной страницы есть область, которая сильно меняется в зависимости от состояния (например: состояние Загрузки -> Ошибка -> Показ данных), эту область лучше превратить в ContentControl, в который подставляются разные UserControl.
   3. Изоляция подписок ReactiveUI: Если во View много логики со сложными Rx-цепочками (this.WhenAnyValue(...)), превращение этого куска в UserControl изолирует эти подписки. Это предотвратит утечки памяти, так как подписки будут выгружаться вместе с жизненным циклом этого маленького UserControl.

## Резюме для работы

* Каждому UserControl — своя пара из файла .axaml.cs с двумя конструкторами (с параметром для DI и пустой для превьюера).
* Все переключения экранов осуществлять через подмену свойства CurrentContent во ViewModel родителя, а само View создавать фабрикой ITransientFactory.
* Все открытия новых Window инкапсулировать в WindowService.

Если вы столкнетесь со специфическими ошибками линкера (Trimming/AOT warning) при компиляции ReactiveUI команд, дайте знать. Я могу показать, как настроить файл rd.xml (Runtime Directives) для подавления этих предупреждений.
Задайте дополнительные вопросы по архитектуре, если:

* У вас планируется модальные диалоги с возвратом результата (например, ShowDialog<TResult>).
* Вам нужно передавать динамические runtime-параметры вглубь дочерних UserControl при создании.

На основе разбора архитектуры Avalonia 12 + ReactiveUI + Native AOT можно четко разделить подходы на рекомендуемые (паттерны) и опасные (антипаттерны).
Так как Native AOT полностью убирает динамическую генерацию кода в рантайме, классические приемы из WPF/старой Avalonia здесь становятся критическими ошибками.
------------------------------
## 🏛️ Архитектурные паттерны (Как делать ПРАВИЛЬНО)

   1. Constructor Injection для View (Внедрение через конструктор)
   * Суть: Передавать ViewModel напрямую в параметры конструктора View(MyViewModel vm). IoC-контейнер сам разрешит все зависимости по цепочке.
      * Плюс: Полная compile-time проверка, отсутствие скрытых зависимостей.
   2. Строгая компиляция привязок (CompiledBindings)
   * Суть: Обязательное использование атрибутов x:CompileBindings="True" и x:DataType="..." в каждом XAML-файле.
      * Плюс: Ошибки в путях свойств выявляются на этапе сборки, а Native AOT компилятор генерирует максимально быстрый прямой C#-код вместо медленной рефлексии.
   3. Паттерн «Сервис Окон» (IWindowService)
   * Суть: Вынос логики создания, конфигурации и вызова методов .Show() или .ShowDialog() для новых окон во внешний Singleton-сервис.
      * Плюс: Разделение ответственности. ViewModels остаются чистыми от UI-кода, их можно покрывать Unit-тестами, подменяя этот сервис через Mock.
   4. Двойной конструктор во View (Паттерн совместимости с Previewer)
   * Суть: Наличие параметризованного конструктора для работы DI в рантайме и пустого конструктора public MyView() { InitializeComponent(); } исключительно для дизайнера IDE.
      * Плюс: Дизайнер XAML работает стабильно, а приложение в рантайме сохраняет архитектурную чистоту.
   5. Композиция ViewModels (Иерархический MVVM)
   * Суть: Главная ViewModel (MainViewModel) управляет жизненным циклом и хранит ссылки на дочерние Transient ViewModels (CurrentPage = dashboardViewModel). Изменение экранов — это просто подмена объекта в свойстве.
      * Плюс: Идеально ложится на реактивный манифест ReactiveUI и легко отслеживается через this.WhenAnyValue(...).
   
------------------------------
## ⚠️ Антипаттерны (Чего нужно ИЗБЕГАТЬ)

   1. Использование Service Locator (App.ServiceProvider.GetService<T>()) внутри UI или VM
   * В чем вред: Использование глобальной точки доступа к контейнеру прямо внутри логики скрывает зависимости класса. Код становится хрупким, его невозможно изолированно тестировать. Контейнер должен вызываться только один раз на самом верхнем уровне (в App.axaml.cs при старте).
   2. Reflection-based ViewLocator (Динамический поиск View по строкам)
   * В чем вред: Классический подход var viewName = vm.GetType().FullName.Replace("ViewModel", "View") гарантированно сломает Native AOT. При сборке триммер (Trimming) вырежет классы View, решив, что они нигде не используются, и приложение упадет в рантайме с NullReferenceException.
   3. Использование оператора new для создания View/ViewModel с зависимостями
   * В чем вред: Если написать var vm = new SettingsViewModel();, то все сервисы, которые требуются этой ViewModel (база данных, API, логирование), придется прокидывать вручную через всю цепочку родительских классов, либо использовать Service Locator. Теряется весь смысл DI.
   4. Регистрация окон и ViewModels как Singleton
   * В чем вред: Если зарегистрировать SettingsViewModel как синглтон, то при повторном открытии окна настроек пользователь увидит старые, не сброшенные данные и прошлые ошибки валидации. Окна и ViewModels обязаны иметь лайфтайм Transient (создаваться заново при каждом запросе) во избежание утечек памяти и багов состояния.
   5. Использование ReactiveUI.Fody ([Reactive] свойства) при Native AOT
   * В чем вред: Fody занимается модификацией IL-кода (ткачеством/weaving) после компиляции. Это часто приводит к непредсказуемому поведению линкера Native AOT. Для свойств в сценариях AOT безопаснее использовать классический RaiseAndSetIfChanged или генератор исходного кода (Source Generator) из CommunityToolkit.Mvvm.
   6. Смешивание контекстов (Отсутствие деления на UserControls)
   * В чем вред: Попытка уместить сложный экран со множеством функций в один массивный файл .axaml на 1000+ строк приводит к тому, что подписки ReactiveUI (WhenAnyValue) начинают пересекаться, а сборщик мусора (GC) не может очистить память, так как мелкие компоненты намертво удерживаются ссылками гигантской родительской View.


