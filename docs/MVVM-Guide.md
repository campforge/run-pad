# Avalonia + MVVM + ReactiveUI + Native AOT — Руководство

> Исчерпывающее руководство по архитектуре десктопного приложения без рефлексии, без Service Locator и без утечек памяти.
> Стек: **Avalonia 12**, **ReactiveUI**, **Microsoft.Extensions.DependencyInjection**, **Native AOT**, **C# 13**.

---

## Содержание

1. [Пять принципов](#пять-принципов)
2. [Настройка проекта](#настройка-проекта)
3. [Жизненные циклы (Lifetimes)](#жизненные-циклы-lifetimes)
4. [Пошаговая инструкция](#пошаговая-инструкция)
   - [Шаг 1: App.axaml.cs — IoC](#шаг-1-appaxamlcs--ioc)
   - [Шаг 2: ViewModel](#шаг-2-viewmodel)
   - [Шаг 3: Window / View](#шаг-3-window--view)
   - [Шаг 4: UserControl + Attach(vm)](#шаг-4-usercontrol--attachvm)
   - [Шаг 5: IWindowService](#шаг-5-iwindowservice)
   - [Шаг 6: AXAML-привязки](#шаг-6-axaml-привязки)
   - [Шаг 7: Подписки и Dispose](#шаг-7-подписки-и-dispose)
5. [Паттерны — как делать правильно](#паттерны--как-делать-правильно)
6. [Антипаттерны — чего избегать](#антипаттерны--чего-избегать)
7. [Анализ и корректировки](#анализ-и-корректировки)

---

## Пять принципов

### 1. Zero-Locator во View и ViewModel
Никакого `App.ServiceProvider.GetService<T>()` внутри окон, UserControl или ViewModel. Все зависимости — только через DI-конструктор. Service Locator допустим лишь в инфраструктурном классе `WindowService`.

### 2. Compile-Time Safety
Native AOT вырежет триммером всё, что не вызвано явно. Все привязки обязаны быть скомпилированными (`x:DataType` на каждом элементе), ViewLocator по строкам запрещён, Fody-weaving запрещён.

### 3. Constructor Injection + Previewer-совместимость
Каждый Window и UserControl имеет два конструктора: параметризованный для DI и пустой для XAML Previewer. DataContext устанавливается в параметризованном конструкторе, а не через XAML к сервис-локатору.

### 4. Transient для VM и Windows
Singleton ViewModel = утечка памяти и «грязное» состояние при повторном открытии. Все ViewModels и Windows регистрируются как `Transient`. Singleton — только для инфраструктурных сервисов.

### 5. ViewModel владеет своим состоянием
View не пишет в ViewModel напрямую и не читает сервисы самостоятельно. Tooltip-тексты, вычисляемые свойства, команды — всё живёт в ViewModel. View только привязывается к готовым свойствам и командам.

---

## Настройка проекта

> **Важно**: одна строка в `.csproj` включает скомпилированные привязки для всего проекта. Не нужно писать `x:CompileBindings="True"` в каждом файле.

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net9.0</TargetFramework>
  <Nullable>enable</Nullable>

  <!-- Включает скомпилированные привязки глобально -->
  <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>

  <!-- Native AOT (опционально) -->
  <PublishAot>true</PublishAot>
  <TrimmerRootDescriptor>rd.xml</TrimmerRootDescriptor>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Avalonia"                                  Version="12.0.5" />
  <PackageReference Include="Avalonia.Desktop"                         Version="12.0.5" />
  <PackageReference Include="ReactiveUI"                               Version="20.*"   />
  <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.*"    />
</ItemGroup>
```

---

## Жизненные циклы (Lifetimes)

| Lifetime | Что регистрировать | Причина |
|---|---|---|
| `AddSingleton` | Инфраструктурные сервисы: `IPreferencesService`, `IThemeService`, `IWindowService`, логгеры | Живут весь срок приложения. Состояние должно сохраняться между открытиями окон. |
| `AddTransient` | Все ViewModels, все Windows, все UserControls | Каждое открытие окна — чистое состояние. GC очистит объект после закрытия. |
| `AddScoped` | **Не использовать** | Scope в десктопном приложении не имеет естественных границ (нет HTTP-запросов), ведёт к утечкам и непредсказуемому поведению. |

---

## Пошаговая инструкция

### Шаг 1: App.axaml.cs — IoC

Инициализируем контейнер и регистрируем все зависимости. MainWindow запрашивается из DI, а не через `new`. Контейнер не хранится как статическое поле — только используется для разрешения корневого объекта.

```csharp
public class App : Application
{
    private ServiceProvider? _serviceProvider;

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

        // Singleton — инфраструктурные сервисы
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IWindowService, WindowService>();

        // Transient — ViewModels, Windows, UserControls
        services.AddTransient<MainWindow>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<PreferencesWindow>();
        services.AddTransient<EditorTabsControl>(); // UserControl

        return services.BuildServiceProvider();
    }
}
```

---

### Шаг 2: ViewModel

ViewModel наследуется от `ReactiveObject`. Свойства используют `RaiseAndSetIfChanged` или C# 13 `field`-keyword (AOT-безопасная альтернатива Fody). Асинхронные команды создаются через `CreateFromTask`, а не через fire-and-forget.

```csharp
public sealed class MainWindowViewModel : ReactiveObject
{
    private readonly IWindowService _windowService;

    // Классический способ (любая версия C#)
    private string _title = "RunPad";
    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    // C# 13 field-keyword (AOT-safe, без Fody/IL-weaving)
    public bool DebugModeEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    // Команды: CreateFromTask для async, Create для sync
    public ReactiveCommand<Unit, Unit> OpenPreferencesCommand { get; }
    public ReactiveCommand<Unit, Unit> RunCommand { get; }

    public MainWindowViewModel(IWindowService windowService, IPreferencesService prefs)
    {
        _windowService = windowService;

        // Правильно: async команда через CreateFromTask
        OpenPreferencesCommand = ReactiveCommand.CreateFromTask(
            _windowService.ShowPreferencesAsync);

        RunCommand = ReactiveCommand.CreateFromTask(RunAsync);

        // Перехват необработанных исключений из команды
        RunCommand.ThrownExceptions.Subscribe(ex => HandleError(ex));
    }

    private async Task RunAsync() { /* ... */ }
}
```

---

### Шаг 3: Window / View

Параметризованный конструктор для DI-рантайма + пустой конструктор для XAML Previewer. `DataContext` устанавливается только в параметризованном конструкторе.

```csharp
public sealed partial class MainWindow : Window, IDisposable
{
    private readonly EditorTabsControl _editorTabs;

    // ── DI-конструктор (рантайм) ─────────────────────────────────
    public MainWindow(
        IPreferencesService prefs,
        EditorTabsControl editorTabsCtrl,
        MainWindowViewModel viewModel)
    {
        _editorTabs = editorTabsCtrl;
        DataContext = viewModel;
        InitializeComponent();

        // Монтируем UserControl в ContentControl-слот
        this.FindControl<ContentControl>("EditorTabsHost")!.Content = editorTabsCtrl;
        editorTabsCtrl.Attach(viewModel);
    }

    // ── Previewer-конструктор (дизайнер) ─────────────────────────
    public MainWindow() => InitializeComponent();

    public void Dispose() { _editorTabs.Dispose(); GC.SuppressFinalize(this); }
}
```

```xml
<!-- MainWindow.axaml — команды вместо Click-handlers -->
<Window xmlns="https://github.com/avaloniaui"
        xmlns:vm="clr-namespace:MyApp.ViewModels"
        x:DataType="vm:MainWindowViewModel">

  <MenuItem Header="Preferences"
            Command="{Binding OpenPreferencesCommand}"/>

  <!-- UserControl монтируется через ContentControl, не напрямую -->
  <ContentControl x:Name="EditorTabsHost" Grid.Column="1"/>

</Window>
```

---

### Шаг 4: UserControl + Attach(vm)

UserControl получает сервисы через DI-конструктор, а ссылку на ViewModel — через метод `Attach(vm)`, который вызывает Window после своей инициализации. Это разделяет момент подключения сервисов и момент подключения данных.

```csharp
public partial class EditorTabsControl : UserControl, IDisposable
{
    private IThemeService? _themeService;
    private IDisposable? _activeTabSub;

    // ── Previewer ─────────────────────────────────────────────────
    public EditorTabsControl() => InitializeComponent();

    // ── DI-рантайм ───────────────────────────────────────────────
    public EditorTabsControl(IThemeService themeService, IPreferencesService prefs)
    {
        InitializeComponent();
        _themeService = themeService;
        _themeService.ThemeChanged += OnThemeChanged;
    }

    // ── Вызывается из Window после его инициализации ─────────────
    public void Attach(MainWindowViewModel vm)
    {
        DataContext = vm;
        vm.Tabs.CollectionChanged += OnTabsCollectionChanged;
        _activeTabSub = vm.WhenAnyValue(x => x.ActiveTab)
                          .Subscribe(OnActiveTabChanged);
    }

    public void Dispose()
    {
        _activeTabSub?.Dispose();
        if (_themeService is not null)
            _themeService.ThemeChanged -= OnThemeChanged;
        GC.SuppressFinalize(this);
    }
}
```

---

### Шаг 5: IWindowService

ViewModel вызывает `IWindowService`, не зная ничего об Avalonia. Сам `WindowService` использует `IServiceProvider` — единственное легитимное место для Service Locator в этой архитектуре.

```csharp
public interface IWindowService
{
    Task ShowPreferencesAsync();
    Task ShowDependenciesAsync();
}

public sealed class WindowService : IWindowService
{
    private readonly IServiceProvider _sp;
    public WindowService(IServiceProvider sp) => _sp = sp;

    public Task ShowPreferencesAsync()
        => ShowDialogAsync(_sp.GetRequiredService<PreferencesWindow>());

    public Task ShowDependenciesAsync()
        => ShowDialogAsync(_sp.GetRequiredService<DependenciesWindow>());

    private static Task ShowDialogAsync(Window dialog)
    {
        var owner = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return owner is not null
            ? dialog.ShowDialog(owner)
            : Task.CompletedTask;
    }
}
```

---

### Шаг 6: AXAML-привязки

При глобальных CompiledBindings каждый корневой элемент **и** каждый `DataTemplate` должны объявлять `x:DataType`. Без него компилятор не знает тип контекста.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:models="clr-namespace:MyApp.ViewModels"
             x:DataType="models:MyViewModel">

  <ItemsControl ItemsSource="{Binding Items}">
    <ItemsControl.ItemTemplate>
      <!-- DataTemplate тоже обязан объявить x:DataType -->
      <DataTemplate x:DataType="models:ItemViewModel">
        <TextBlock Text="{Binding Name}"/>
      </DataTemplate>
    </ItemsControl.ItemTemplate>
  </ItemsControl>

  <!-- Кнопка привязана к команде, не к Click-handler -->
  <Button Command="{Binding OpenSettingsCommand}" Content="Настройки"/>

</UserControl>
```

---

### Шаг 7: Подписки и Dispose

Rx-подписки внутри UserControl привязываются к жизненному циклу через `WhenActivated` + `DisposeWith`. Если UserControl монтируется динамически, это единственный способ гарантировать отписку при размонтировании.

```csharp
// Паттерн WhenActivated (для UserControl с IViewFor<TViewModel>)
public partial class MyPanel : UserControl, IViewFor<MyPanelViewModel>
{
    public MyPanel()
    {
        InitializeComponent();
        this.WhenActivated(disposables =>
        {
            this.WhenAnyValue(x => x.ViewModel!.Status)
                .Subscribe(s => UpdateStatus(s))
                .DisposeWith(disposables);
        });
    }
}
```

```csharp
// ViewModel сама подписывается на сервис и отписывается в Dispose
public sealed class EditorTabViewModel : ReactiveObject, IDisposable
{
    private readonly IPreferencesService _prefs;

    public string TooltipRun
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "Run";

    public EditorTabViewModel(IPreferencesService prefs)
    {
        _prefs = prefs;
        SyncTooltips();
        _prefs.Changed += OnPreferencesChanged; // ViewModel сама подписывается
    }

    private void OnPreferencesChanged() => SyncTooltips();
    private void SyncTooltips() => TooltipRun = Label("Run", _prefs.Keyboard.Run);

    public void Dispose()
    {
        _prefs.Changed -= OnPreferencesChanged; // Явная отписка
        GC.SuppressFinalize(this);
    }
}
```

---

## Паттерны — как делать правильно

### ✅ Constructor Injection для всех зависимостей
Window, UserControl и ViewModel получают все зависимости через конструктор. DI-контейнер строит весь граф автоматически. Нет скрытых зависимостей — всё видно в сигнатуре.

```csharp
public MainWindow(IPreferencesService prefs, MainWindowViewModel vm) { ... }
```

### ✅ Двойной конструктор (Previewer-совместимость)
Параметризованный конструктор для DI + пустой конструктор только для XAML Previewer. DataContext устанавливается только в параметризованном.

```csharp
public MyView() => InitializeComponent(); // только InitializeComponent, никакой логики
```

### ✅ IWindowService для открытия окон
ViewModel вызывает интерфейс сервиса, не зная ни о каких Avalonia-типах. ViewModel остаётся тестируемой — сервис легко мокируется.

### ✅ ReactiveCommand.CreateFromTask для async
Команда автоматически управляет флагом `IsExecuting` и предотвращает повторный запуск.

```csharp
RunCommand = ReactiveCommand.CreateFromTask(RunAsync);
```

### ✅ ThrownExceptions.Subscribe на командах
Необработанные исключения из ReactiveCommand терминируют Rx-поток и могут упасть тихо.

```csharp
RunCommand.ThrownExceptions.Subscribe(LogError);
```

### ✅ ViewModel сама управляет tooltip-свойствами
Tooltip-тексты, вычисляемые свойства — это состояние ViewModel. ViewModel подписывается на `IPreferencesService.Changed` в конструкторе и обновляет свои свойства. View только привязывается.

### ✅ Attach(vm) для UserControl с DI
Когда UserControl требует и сервисы (DI), и ViewModel (передаётся позже) — DI-конструктор получает сервисы, метод `Attach(vm)` вызывается из Window после монтирования.

### ✅ C# 13 field-keyword вместо Fody
AOT-safe, без IL-weaving, без отдельного backing field.

```csharp
public bool Flag { get; set => this.RaiseAndSetIfChanged(ref field, value); }
```

### ✅ Transient для VM и Windows
Каждое открытие окна создаёт новый экземпляр ViewModel через DI. При закрытии GC уберёт оба объекта. Никакого «грязного состояния» при повторном открытии.

### ✅ x:DataType на каждом элементе с привязкой
При `AvaloniaUseCompiledBindingsByDefault=true` каждый корневой элемент и каждый `DataTemplate` обязан объявлять `x:DataType`. Без него компилятор не может сгенерировать типобезопасный код.

---

## Антипаттерны — чего избегать

### ❌ Service Locator в View или ViewModel
`App.ServiceProvider.GetService<T>()` внутри окна или VM скрывает зависимости, делает класс нетестируемым и нарушает принцип инверсии зависимостей. Допустимо только в `WindowService`.

### ❌ Reflection-based ViewLocator
```csharp
// НЕЛЬЗЯ: триммер AOT вырежет View-классы
var viewName = vm.GetType().FullName.Replace("ViewModel", "View");
```
Приложение упадёт с `NullReferenceException` в рантайме.

### ❌ new MyWindow() или new MyViewModel()
Ручное создание объектов с зависимостями ломает DI-цепочку. Используйте DI-контейнер или `IWindowService`.

### ❌ Singleton для ViewModels и Windows
При повторном открытии пользователь увидит старые данные и ошибки валидации. Singleton ViewModel держит в памяти весь граф данных вечно.

### ❌ ReactiveUI.Fody [Reactive] при Native AOT
Fody модифицирует IL-код после компиляции. Native AOT компилирует IL в нативный код до модификации Fody — результат непредсказуем. Используйте `RaiseAndSetIfChanged` или C# 13 `field`-keyword.

### ❌ Fire-and-forget в ReactiveCommand
```csharp
// НЕЛЬЗЯ: IsExecuting не работает, исключения теряются
ReactiveCommand.Create(() => { _ = AsyncMethod(); });

// Правильно:
ReactiveCommand.CreateFromTask(AsyncMethod);
```

### ❌ View пишет в ViewModel напрямую
`SyncHotkeyLabels()` в code-behind читает сервис и пишет в ViewModel — нарушение направления потока данных. ViewModel должна сама обновлять своё состояние.

### ❌ Click-handler вместо Command
```xml
<!-- НЕЛЬЗЯ -->
<Button Click="OpenPreferences_Click" Content="Настройки"/>

<!-- Правильно -->
<Button Command="{Binding OpenPreferencesCommand}" Content="Настройки"/>
```

### ❌ Activator.CreateInstance в фабрике
```csharp
// НЕЛЬЗЯ: триммер AOT удалит конструктор как «неиспользуемый»
return (T)Activator.CreateInstance(typeof(T), viewModel)!;
```
Регистрируйте тип явно в DI и запрашивайте через `GetRequiredService<T>`.

### ❌ Смешивание ReactiveUI и CommunityToolkit.Mvvm
`ObservableObject` + `[ObservableProperty]` (CommunityToolkit) и `ReactiveObject` + `WhenAnyValue` (ReactiveUI) — несовместимые экосистемы. Выберите одну и придерживайтесь её во всём проекте.

---

## Анализ и корректировки

Разбор конкретных проблем, которые встречаются в типовых руководствах по теме.

### 1. App.ServiceProvider как статическое поле — антипаттерн

**Проблема:** `public static IServiceProvider ServiceProvider { get; private set; }` — глобальная точка доступа к контейнеру.

**Решение:** Хранить `ServiceProvider` как приватное поле экземпляра `App`. `WindowService` принимает `IServiceProvider` через конструктор — только он имеет право его использовать. Все остальные получают зависимости через Constructor Injection.

---

### 2. ITransientFactory.ViewWithVm использует Activator.CreateInstance

**Проблема:** `(T)Activator.CreateInstance(typeof(T), viewModel)!` — триммер AOT удалит конструктор, приложение упадёт.

**Решение:** UserControl с DI-конструктором нужно регистрировать в DI как `Transient` и получать через `GetRequiredService<T>`. Для передачи ViewModel использовать паттерн `Attach(vm)`.

---

### 3. ActivatorUtilities.CreateInstance требует аннотаций для строгого AOT

**Проблема:** `ActivatorUtilities.CreateInstance<T>` для фабрик с runtime-параметрами без `[DynamicallyAccessedMembers]` или `rd.xml` выдаст предупреждения или упадёт при строгом AOT.

**Решение:** Если нужны runtime-параметры, передавать их через явный конструктор или фабричный метод самого сервиса, не через `ActivatorUtilities`.

---

### 4. Смешение CommunityToolkit и ReactiveUI в примерах

**Проблема:** Раздел про навигацию использует `ObservableObject` + `[ObservableProperty]` (CommunityToolkit.Mvvm), тогда как остальные разделы описывают ReactiveUI. Это несовместимые экосистемы.

**Решение:** Для проекта с ReactiveUI — везде использовать `ReactiveObject`, `RaiseAndSetIfChanged`, `WhenAnyValue`. `[ObservableProperty]` из CommunityToolkit.Mvvm не использовать.

---

### 5. Per-file x:CompileBindings vs глобальный .csproj-флаг

**Проблема:** `x:CompileBindings="True"` в каждом файле легко забыть, нет гарантии единообразия.

**Решение:** Одна строка в `.csproj` — `<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>` — включает компиляцию для всего проекта. Per-file флаг нужен только для точечного отключения.

---

### 6. Отсутствует WhenActivated + DisposeWith для UserControl

**Проблема:** Ручной `Subscribe` без `DisposeWith`. Если UserControl монтируется динамически, подписки накапливаются и не освобождаются.

**Решение:** Использовать `this.WhenActivated(d => { subscription.DisposeWith(d); })`. Avalonia вызовет dispose при размонтировании UserControl. Или явно реализовывать `IDisposable` с ручной отпиской.

---

### 7. Fire-and-forget в примере команды открытия окна

**Проблема:** `ReactiveCommand.Create(() => { windowService.ShowSettingsWindow(); })` — если метод async, это fire-and-forget. Исключения теряются.

**Решение:** `ReactiveCommand.CreateFromTask(() => windowService.ShowSettingsAsync())` с подпиской на `ThrownExceptions`.

---

### 8. DataTemplate создаёт View через пустой конструктор — конфликт с DI

**Проблема:** Если View зарегистрирована в DI с DI-конструктором, но `DataTemplate` в `App.axaml` вызывает её создание — Avalonia использует пустой конструктор. DI-зависимости View не будут внедрены.

**Решение:** Для Views, используемых в DataTemplate, пустой конструктор должен быть полностью самодостаточен (без DI-зависимостей), либо использовать паттерн `Attach(vm)`.

---

### 9. Не упомянут C# 13 field-keyword

**Проблема:** Перечисляются Fody, CommunityToolkit и ручной `ref _field` как опции для свойств. Не упоминается C# 13 `field`-keyword.

**Решение:** C# 13 `field`-keyword — лучшая опция для проектов с AOT:

```csharp
public bool Flag { get; set => this.RaiseAndSetIfChanged(ref field, value); }
```

Нет отдельного backing field, нет IL-weaving, полная AOT-совместимость.

---

### 10. ITransientFactory не нужна при правильной DI-архитектуре

**Проблема:** `ITransientFactory` с методами `Create<T>()` и `ViewWithVm<T>()` — по сути ещё один Service Locator, обёрнутый в интерфейс, плюс нарушающий AOT `Activator.CreateInstance`.

**Решение:** Если нужно создать UserControl с ViewModel — регистрировать оба как `Transient` и получать из DI напрямую. Для UserControl со сложной инициализацией — паттерн `Attach(vm)` без фабрики.
