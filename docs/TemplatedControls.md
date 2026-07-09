В Avalonia UI TemplatedControl (шаблонный элемент управления) принципиально отличается от UserControl. Если UserControl жестко связывает разметку и логику, то TemplatedControl разделяет их: вся логика пишется в C#-классе, а внешний вид полностью выносится в XAML-шаблон (обычно в файл .axaml в папке Themes или Styles).
Это идеальный подход для создания переиспользуемых библиотек компонентов (дизайн-систем), которые должны поддерживать ReactiveUI, DI и гибкую кастомизацию.
Ниже представлено подробное архитектурное руководство по созданию TemplatedControl с поддержкой кастомных событий, интеграцией в ReactiveUI и работой через DI.
------------------------------
## Руководство: Маршрутизируемые события в TemplatedControl (ReactiveUI + DI)## Главное отличие в архитектуре
В TemplatedControl у нас нет прямого Code-Behind для разметки. Вместо этого элемент управления переопределяет метод OnApplyTemplate(). В этом методе он находит элементы внутри своего шаблона (например, кнопки), подписывается на их локальные действия и транслирует их наружу в виде RoutedEvent.
------------------------------
## Шаг 1. Создание C# класса элемента управления
Создадим кастомный элемент управления CustomCard.cs. Он будет содержать регистрацию события и логику захвата элементов из будущего шаблона.
Файл: CustomCard.cs

using System;using Avalonia;using Avalonia.Controls;using Avalonia.Controls.Primitives;using Avalonia.Interactivity;
namespace AvaloniaApp.Controls;
// Класс для передачи данных из TemplatedControl наружуpublic class CardSubmittedEventArgs : RoutedEventArgs
{
public string Value { get; }
public CardSubmittedEventArgs(RoutedEvent routedEvent, string value) : base(routedEvent)
{
Value = value;
}
}
public class CustomCard : TemplatedControl
{
// 1. Регистрируем Bubble-событие, которое будет всплывать по дереву UI
public static readonly RoutedEvent<CardSubmittedEventArgs> SubmittedEvent =
RoutedEvent.Register<CustomCard, CardSubmittedEventArgs>(
nameof(Submitted),
RoutingStrategies.Bubble);

    public event EventHandler<CardSubmittedEventArgs>? Submitted
    {
        add => AddHandler(SubmittedEvent, value);
        remove => RemoveHandler(SubmittedEvent, value);
    }

    // Ссылки на элементы внутри шаблона, которые понадобятся для логики
    private Button? _submitButton;
    private TextBox? _inputTextBox;

    // 2. Метод вызывается, когда Avalonia применила XAML-шаблон к этому контролу
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        // Отписываемся от старых элементов, если шаблон переприменяется (для предотвращения утечек памяти)
        if (_submitButton != null)
            _submitButton.Click -= OnSubmitButtonClick;

        // Находим нужные элементы внутри шаблона по их именам (PART_...)
        _submitButton = e.NameScope.Find<Button>("PART_SubmitButton");
        _inputTextBox = e.NameScope.Find<TextBox>("PART_InputTextBox");

        // Подписываемся на событие клика внутренней кнопки
        if (_submitButton != null)
            _submitButton.Click += OnSubmitButtonClick;
    }

    private void OnSubmitButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_inputTextBox == null) return;

        string currentText = _inputTextBox.Text ?? string.Empty;

        // 3. Генерируем наше кастомное событие наружу
        var args = new CardSubmittedEventArgs(SubmittedEvent, currentText);
        RaiseEvent(args);
    }
}

Важно по именованию: В TemplatedControl принято называть элементы внутри шаблона с префиксом PART_ (например, PART_SubmitButton). Это дает понять разработчикам, что данные элементы критически важны для работы логики класса.

------------------------------
## Шаг 2. Определение XAML-шаблона (Стиля)
Теперь опишем, как наш CustomCard должен выглядеть. Создадим файл стилей.
Файл: CustomCardStyle.axaml

<Styles xmlns="https://github.com"
xmlns:x="http://microsoft.com"
xmlns:controls="using:AvaloniaApp.Controls">

    <!-- Нацеливаем стиль на наш кастомный класс -->
    <Style Selector="controls|CustomCard">
        <!-- Задаем дефолтные свойства, если они не указаны явно -->
        <Setter Property="Background" Value="#F5F5F5"/>
        <Setter Property="BorderBrush" Value="#E0E0E0"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Padding" Value="15"/>

        <!-- Определяем сам шаблон -->
        <Setter Property="Template">
            <ControlTemplate>
                <Border Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}"
                        Padding="{TemplateBinding Padding}"
                        CornerRadius="8">

                    <StackPanel Spacing="10">
                        <TextBlock Text="Компонент: TemplatedControl" FontWeight="Bold"/>

                        <!-- Имена должны строго совпадать с тем, что мы ищем в OnApplyTemplate -->
                        <TextBox x:Name="PART_InputTextBox" Watermark="Введите текст..." />
                        <Button x:Name="PART_SubmitButton" Content="Подтвердить" HorizontalAlignment="Right"/>
                    </StackPanel>

                </Border>
            </ControlTemplate>
        </Setter>
    </Style>
</Styles>

Чтобы проект увидел этот стиль, его нужно зарегистрировать в файле App.axaml:

<Application.Styles>
<FluentTheme />
<!-- Подключаем наш файл со стилем элемента -->
<StyleInclude Source="using:AvaloniaApp.Controls/CustomCardStyle.axaml" />
</Application.Styles>

------------------------------
## Шаг 3. Создание Родительской ViewModel и Сервиса (DI + ReactiveUI)
ViewModel здесь абсолютно идентична той, что используется для UserControl. Ей всё равно, какой именно тип элемента сгенерировал событие. Она работает только с чистыми данными и зависит от сервисов через DI.

using System;using System.Reactive;using ReactiveUI;
namespace AvaloniaApp.ViewModels;
public interface INotificationService { void Send(string message); }
public class MainViewModel : ViewModelBase
{
private readonly INotificationService _notificationService;
private string _logText = "Сообщений нет";

    public string LogText
    {
        get => _logText;
        set => this.RaiseAndSetIfChanged(ref _logText, value);
    }

    // Команда ReactiveUI для обработки данных
    public ReactiveCommand<string, Unit> ProcessTextCommand { get; }

    // Конструктор, в который DI-контейнер передаст нужный сервис
    public MainViewModel(INotificationService notificationService)
    {
        _notificationService = notificationService;

        ProcessTextCommand = ReactiveCommand.Create<string>(text =>
        {
            // Вызываем логику внешнего сервиса
            _notificationService.Send(text);

            // Обновляем UI-состояние во ViewModel
            LogText = $"[DI Сервис]: Данные '{text}' успешно отправлены!";
        });
    }
}

------------------------------
## Шаг 4. Использование в родительском View (XAML)
В родительском окне мы внедряем CustomCard и с помощью Avalonia.Xaml.Behaviors связываем всплывающее событие Submitted напрямую с ProcessTextCommand.
Файл: MainWindow.axaml

<Window xmlns="https://github.com"
xmlns:x="http://microsoft.com"
xmlns:vm="using:AvaloniaApp.ViewModels"
xmlns:controls="using:AvaloniaApp.Controls"
xmlns:converters="using:AvaloniaApp.Converters"
xmlns:i="using:Avalonia.Xaml.Interactivity"
xmlns:ia="using:Avalonia.Xaml.Interactions.Core"
x:Class="AvaloniaApp.Views.MainWindow"
x:DataType="vm:MainViewModel"
Title="TemplatedControl + ReactiveUI" Width="450" Height="350"
WindowStartupLocation="CenterScreen">

    <!-- Регистрируем конвертер аргументов внутри ресурсов окна (или в App.axaml) -->
    <Window.Resources>
        <converters:CardSubmittedConverter x:Key="CardSubmittedConverter"/>
    </Window.Resources>

    <StackPanel Margin="20" Spacing="15" VerticalAlignment="Center">

        <TextBlock Text="{Binding LogText}" Foreground="DarkBlue" FontSize="14" HorizontalAlignment="Center"/>

        <!-- Используем наш TemplatedControl -->
        <controls:CustomCard Background="#FFF9E7" BorderBrush="Orange">
            <i:Interaction.Behaviors>
                <!-- Слушаем событие, объявленное в C#-классе CustomCard.cs -->
                <ia:EventTriggerBehavior EventName="Submitted">
                    <!-- Вызываем ReactiveCommand из MainViewModel -->
                    <ia:InvokeCommandAction
                        Command="{Binding ProcessTextCommand}"
                        InputConverter="{StaticResource CardSubmittedConverter}"/>
                </ia:EventTriggerBehavior>
            </i:Interaction.Behaviors>
        </controls:CustomCard>

    </StackPanel>
</Window>

## Конвертер аргументов события
Для извлечения свойства Value из CardSubmittedEventArgs и передачи его строкой в команду, используется простой конвертер:

using System;using System.Globalization;using Avalonia.Data.Converters;using AvaloniaApp.Controls;
namespace AvaloniaApp.Converters;
public class CardSubmittedConverter : IValueConverter
{
public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
{
if (value is CardSubmittedEventArgs args)
{
return args.Value; // Возвращаем чистую строку для ReactiveCommand<string, Unit>
}
return null;
}

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

------------------------------
## Шаг 5. Конфигурация DI-контейнера
При инициализации приложения регистрируем все зависимости, включая сервисы, Окна и Вью-Модели в App.axaml.cs.

using System;using Avalonia;using Avalonia.Controls.ApplicationLifetimes;using Avalonia.Markup.Xaml;using AvaloniaApp.ViewModels;using AvaloniaApp.Views;using Microsoft.Extensions.DependencyInjection;
namespace AvaloniaApp;
public class ConsoleNotificationService : INotificationService
{
public void Send(string message) => Console.WriteLine($"[DI Бэкенд] Лог: {message}");
}
public partial class App : Application
{
public IServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var serviceCollection = new ServiceCollection();

        // Регистрируем синглтон сервиса уведомлений
        serviceCollection.AddSingleton<INotificationService, ConsoleNotificationService>();

        // Регистрируем UI
        serviceCollection.AddTransient<MainViewModel>();
        serviceCollection.AddTransient<MainWindow>();

        Services = serviceCollection.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = Services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}

------------------------------
## Архитектурные выводы

* Полное разделение (Decoupling): Логика TemplatedControl сосредоточена в .cs файле, разметка — в файле стилей .axaml. Внешний вид можно полностью переписать (изменить цвета, структуру, анимации), не ломая логику событий и отправки данных.
* Чистый MVVM: Окно знает о существовании контрола, контрол знает о своих внутренних элементах, но никто из них ничего не знает про конкретную ViewModel или DI-сервисы.
* Готовность к переиспользованию: Такой контрол можно вынести в отдельную Class Library (NuGet-пакет) и использовать во множестве разных проектов.


