В современной разработке на Avalonia UI с паттерном MVVM (ReactiveUI) и Dependency Injection (DI) практика написания логики в Code-Behind (.axaml.cs) считается устаревшей. Вместо перехвата событий в коде окон, правильный архитектурный подход — связывать события (RoutedEvents) или действия UI напрямую с командами во ViewModel (ReactiveCommand).
Ниже представлено полное руководство, переписанное под стек ReactiveUI + DI. Для связывания события из XAML с ViewModel используется стандартный пакет Avalonia.Xaml.Behaviors.
------------------------------
## Архитектурное руководство: События UserControl в связке с ReactiveUI и DI## Архитектурный принцип

   1. UserControl по-прежнему генерирует RoutedEvent внутри себя (это делает его независимым UI-компонентом).
   2. Родительский View с помощью EventTriggerBehavior перехватывает это событие прямо в XAML.
   3. Перехваченное событие вызывает ReactiveCommand на Родительской ViewModel, которая была внедрена через DI (например, через Microsoft.Extensions.DependencyInjection).

------------------------------
## Подготовка: Установка пакета Behaviors
Для того чтобы связывать события элементов с командами во ViewModel без написания кода в файле .axaml.cs, установите NuGet-пакет:

dotnet add package Avalonia.Xaml.Behaviors

Добавьте пространство имен во все ваши .axaml файлы, где будете его использовать:
xmlns:i="using:Avalonia.Xaml.Interactivity" и xmlns:ia="using:Avalonia.Xaml.Interactions.Core"
------------------------------
## Шаг 1. Создание независимого UserControl
Компонент должен оставаться "глупым" (UI-only). Он ничего не знает про DI или родительские ViewModel. Он просто умеет уведомлять внешнюю систему о том, что что-то произошло, и передавать данные.
## 1. Разметка: CardComponent.axaml

<UserControl xmlns="https://github.com"
             xmlns:x="http://microsoft.com"
             x:Class="AvaloniaApp.Views.CardComponent">
    <Border BorderBrush="Gray" BorderThickness="1" CornerRadius="8" Padding="15">
        <StackPanel Spacing="10">
            <TextBox x:Name="InputTextBox" Watermark="Введите текст для отправки..." />
            <Button Content="Отправить родителю" Click="OnSendButtonClick" />
        </StackPanel>
    </Border>
</UserControl>

## 2. Аргументы события: CardEventArgs.cs
Создадим класс для безопасной передачи данных наверх.

using Avalonia.Interactivity;
namespace AvaloniaApp.Events;
public class CardEventArgs : RoutedEventArgs
{
    public string Data { get; }

    public CardEventArgs(RoutedEvent routedEvent, string data) : base(routedEvent)
    {
        Data = data;
    }
}

## 3. Логика компонента: CardComponent.axaml.cs

using System;using Avalonia.Controls;using Avalonia.Interactivity;using AvaloniaApp.Events;
namespace AvaloniaApp.Views;
public partial class CardComponent : UserControl
{
    // Регистрируем кастомное всплывающее (Bubble) событие
    public static readonly RoutedEvent<CardEventArgs> DataSubmittedEvent =
        RoutedEvent.Register<CardComponent, CardEventArgs>(
            nameof(DataSubmitted), 
            RoutingStrategies.Bubble);

    public event EventHandler<CardEventArgs>? DataSubmitted
    {
        add => AddHandler(DataSubmittedEvent, value);
        remove => RemoveHandler(DataSubmittedEvent, value);
    }

    public CardComponent() => InitializeComponent();

    private void OnSendButtonClick(object? sender, RoutedEventArgs e)
    {
        var textToSend = InputTextBox.Text ?? string.Empty;
        
        // Создаем аргументы и генерируем событие наверх по дереву UI
        var args = new CardEventArgs(DataSubmittedEvent, textToSend);
        RaiseEvent(args);
    }
}

------------------------------
## Шаг 2. Создание Родительской ViewModel (с DI)
Предположим, у нас есть MainViewModel, которая управляет главным экраном. Она принимает через конструктор сервис (например, для логирования или отправки данных на сервер), зарегистрированный в DI-контейнере.

using System;using System.Reactive;using ReactiveUI;
namespace AvaloniaApp.ViewModels;
// Демонстрационный сервис для DIpublic interface IDataProcessor { void Process(string input); }
public class MainViewModel : ViewModelBase
{
    private readonly IDataProcessor _dataProcessor;
    private string _statusMessage = "Ожидание данных...";

    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    // ReactiveCommand, которая будет принимать строку из UserControl
    public ReactiveCommand<string, Unit> HandleCardDataCommand { get; }

    // Конструктор поддерживает полноценный Dependency Injection
    public MainViewModel(IDataProcessor dataProcessor)
    {
        _dataProcessor = dataProcessor;

        // Инициализируем команду ReactiveUI
        HandleCardDataCommand = ReactiveCommand.Create<string>(ExecuteHandleCardData);
    }

    private void ExecuteHandleCardData(string incomingData)
    {
        // Вызываем внедренный через DI сервис
        _dataProcessor.Process(incomingData);
        
        // Обновляем состояние UI
        StatusMessage = $"[DI Успех] Получено и обработано: {incomingData}";
    }
}

------------------------------
## Шаг 3. Связывание события с командой в родительском View
В MainWindow.axaml мы объявляем наш CardComponent. С помощью Interactivity мы перехватываем событие DataSubmitted и перенаправляем переданные в него данные прямо в команду HandleCardDataCommand.

<Window xmlns="https://github.com"
        xmlns:x="http://microsoft.com"
        xmlns:vm="using:AvaloniaApp.ViewModels"
        xmlns:views="using:AvaloniaApp.Views"
        xmlns:i="using:Avalonia.Xaml.Interactivity"
        xmlns:ia="using:Avalonia.Xaml.Interactions.Core"
        x:Class="AvaloniaApp.Views.MainWindow"
        x:DataType="vm:MainViewModel"
        Title="ReactiveUI + DI Events Example" Width="500" Height="400"
        WindowStartupLocation="CenterScreen">

    <StackPanel Margin="30" Spacing="20" VerticalAlignment="Center">
        
        <TextBlock Text="Родительское окно" FontSize="20" FontWeight="Bold"/>
        <TextBlock Text="{Binding StatusMessage}" Foreground="Green" FontWeight="SemiBold"/>
        
        <Separator Grid.Row="1" Background="LightGray"/>

        <!-- Внедряем наш кастомный UserControl -->
        <views:CardComponent>
            <i:Interaction.Behaviors>
                <!-- 1. Слушаем событие DataSubmitted, генерируемое внутри компонента -->
                <ia:EventTriggerBehavior EventName="DataSubmitted">
                    <!-- 2. Вызываем команду во ViewModel родителя -->
                    <!-- Передаем свойство 'Data' из нашего CardEventArgs в качестве параметра команды -->
                    <ia:InvokeCommandAction 
                        Command="{Binding HandleCardDataCommand}" 
                        CommandParameter="{Binding $parent[views:CardComponent].DataContext, RelativeSource={RelativeSource Self}}"
                        InputConverter="{StaticResource CardEventArgsConverter}"/> 
                </ia:EventTriggerBehavior>
            </i:Interaction.Behaviors>
        </views:CardComponent>

    </StackPanel>
</Window>

Важное примечание по InputConverter:
Чтобы вытащить свойство Data из CardEventArgs и передать его в команду в качестве чистого string, вам потребуется простой конвертер (IValueConverter). Вот его код:

using System;using System.Globalization;using Avalonia.Data.Converters;using AvaloniaApp.Events;
namespace AvaloniaApp.Converters;
public class CardEventArgsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Извлекаем кастомные данные из аргументов события Avalonia
        if (value is CardEventArgs args)
        {
            return args.Data;
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) 
        => throw new NotImplementedException();
}

(Не забудьте зарегистрировать этот конвертер в App.axaml в секции <Application.Resources> чтобы XAML его видел).
------------------------------
## Шаг 4. Настройка DI Контейнера (Регистрация)
Чтобы всё это взлетело, ваши View и ViewModel должны правильно создаваться через ваш IoC-контейнер (например, Microsoft.Extensions.DependencyInjection).
Типичная настройка в App.axaml.cs:

using System;using Avalonia;using Avalonia.Controls.ApplicationLifetimes;using Avalonia.Markup.Xaml;using AvaloniaApp.ViewModels;using AvaloniaApp.Views;using Microsoft.Extensions.DependencyInjection;
namespace AvaloniaApp;
public class MyDataProcessor : IDataProcessor
{
    public void Process(string input) => Console.WriteLine($"DI сервис обработал: {input}");
}
public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 1. Создаем коллекцию сервисов (DI Контейнер)
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        Services = serviceCollection.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 2. Запрашиваем окно и вьюмодель из контейнера
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = Services.GetRequiredService<MainViewModel>();
            
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Регистрация бизнес-логики
        services.AddSingleton<IDataProcessor, MyDataProcessor>();

        // Регистрация UI слоев
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();
    }
}

------------------------------
## Резюме паттерна

* UserControl остался чистым UI-компонентом без ссылок на архитектуру приложения.
* Данные безопасно поднялись через слои абстракции UI с помощью стандартных механизмов Avalonia (RoutedEvent).
* Behaviors связал UI-событие с ReactiveCommand без засорения MainWindow.axaml.cs.
* Родителем управляет полноценная ViewModel, которая легко покрывается Unit-тестами и создается через DI.

Если вы хотите оптимизировать этот процесс и уйти от создания конвертеров (CardEventArgsConverter), я могу показать, как связать UserControl с родителем через двусторонний Binding свойств (AvaloniaProperty) вместо событий. Как вам такой вариант?

