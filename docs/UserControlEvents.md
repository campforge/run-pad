In modern Avalonia UI development with the MVVM pattern (ReactiveUI) and Dependency Injection (DI), writing logic in code-behind (.axaml.cs) is considered outdated. Instead of handling events in window code, the correct architectural approach is to bind events (RoutedEvents) or UI actions directly to commands in the ViewModel (ReactiveCommand).
Below is a complete guide written for the ReactiveUI + DI stack. The standard Avalonia.Xaml.Behaviors package is used to bind an event from XAML to the ViewModel.
------------------------------
## Architecture guide: UserControl events with ReactiveUI and DI
## Architectural principle

   1. The UserControl still raises a RoutedEvent internally (this keeps it an independent UI component).
   2. The parent View intercepts this event directly in XAML using EventTriggerBehavior.
   3. The intercepted event invokes a ReactiveCommand on the parent ViewModel, which was injected via DI (for example, via Microsoft.Extensions.DependencyInjection).

------------------------------
## Preparation: installing the Behaviors package
To bind element events to ViewModel commands without writing code in the .axaml.cs file, install the NuGet package:

dotnet add package Avalonia.Xaml.Behaviors

Add the namespaces to every .axaml file where you will use it:
xmlns:i="using:Avalonia.Xaml.Interactivity" and xmlns:ia="using:Avalonia.Xaml.Interactions.Core"
------------------------------
## Step 1. Creating an independent UserControl
The component must stay "dumb" (UI-only). It knows nothing about DI or parent ViewModels. It simply knows how to notify the outside world that something happened and pass data along.
## 1. Markup: CardComponent.axaml

<UserControl xmlns="https://github.com"
             xmlns:x="http://microsoft.com"
             x:Class="AvaloniaApp.Views.CardComponent">
    <Border BorderBrush="Gray" BorderThickness="1" CornerRadius="8" Padding="15">
        <StackPanel Spacing="10">
            <TextBox x:Name="InputTextBox" Watermark="Enter text to send..." />
            <Button Content="Send to parent" Click="OnSendButtonClick" />
        </StackPanel>
    </Border>
</UserControl>

## 2. Event arguments: CardEventArgs.cs
Create a class for passing data upward safely.

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

## 3. Component logic: CardComponent.axaml.cs

using System;using Avalonia.Controls;using Avalonia.Interactivity;using AvaloniaApp.Events;
namespace AvaloniaApp.Views;
public partial class CardComponent : UserControl
{
    // Register a custom bubbling (Bubble) event
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
        
        // Create the arguments and raise the event up the UI tree
        var args = new CardEventArgs(DataSubmittedEvent, textToSend);
        RaiseEvent(args);
    }
}

------------------------------
## Step 2. Creating the parent ViewModel (with DI)
Suppose we have a MainViewModel that manages the main screen. Its constructor takes a service (for example, for logging or sending data to a server) registered in the DI container.

using System;using System.Reactive;using ReactiveUI;
namespace AvaloniaApp.ViewModels;
// Demo service for DI
public interface IDataProcessor { void Process(string input); }
public class MainViewModel : ViewModelBase
{
    private readonly IDataProcessor _dataProcessor;
    private string _statusMessage = "Waiting for data...";

    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    // ReactiveCommand that will receive a string from the UserControl
    public ReactiveCommand<string, Unit> HandleCardDataCommand { get; }

    // The constructor supports full Dependency Injection
    public MainViewModel(IDataProcessor dataProcessor)
    {
        _dataProcessor = dataProcessor;

        // Initialize the ReactiveUI command
        HandleCardDataCommand = ReactiveCommand.Create<string>(ExecuteHandleCardData);
    }

    private void ExecuteHandleCardData(string incomingData)
    {
        // Call the service injected via DI
        _dataProcessor.Process(incomingData);
        
        // Update the UI state
        StatusMessage = $"[DI Success] Received and processed: {incomingData}";
    }
}

------------------------------
## Step 3. Binding the event to a command in the parent View
In MainWindow.axaml we declare our CardComponent. Using Interactivity, we intercept the DataSubmitted event and forward the data passed with it directly to the HandleCardDataCommand command.

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
        
        <TextBlock Text="Parent window" FontSize="20" FontWeight="Bold"/>
        <TextBlock Text="{Binding StatusMessage}" Foreground="Green" FontWeight="SemiBold"/>
        
        <Separator Grid.Row="1" Background="LightGray"/>

        <!-- Embed our custom UserControl -->
        <views:CardComponent>
            <i:Interaction.Behaviors>
                <!-- 1. Listen for the DataSubmitted event raised inside the component -->
                <ia:EventTriggerBehavior EventName="DataSubmitted">
                    <!-- 2. Invoke the command on the parent ViewModel -->
                    <!-- Pass the 'Data' property of our CardEventArgs as the command parameter -->
                    <ia:InvokeCommandAction 
                        Command="{Binding HandleCardDataCommand}" 
                        CommandParameter="{Binding $parent[views:CardComponent].DataContext, RelativeSource={RelativeSource Self}}"
                        InputConverter="{StaticResource CardEventArgsConverter}"/> 
                </ia:EventTriggerBehavior>
            </i:Interaction.Behaviors>
        </views:CardComponent>

    </StackPanel>
</Window>

Important note on InputConverter:
To extract the Data property from CardEventArgs and pass it to the command as a plain string, you need a simple converter (IValueConverter). Here is its code:

using System;using System.Globalization;using Avalonia.Data.Converters;using AvaloniaApp.Events;
namespace AvaloniaApp.Converters;
public class CardEventArgsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Extract the custom data from the Avalonia event arguments
        if (value is CardEventArgs args)
        {
            return args.Data;
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) 
        => throw new NotImplementedException();
}

(Do not forget to register this converter in App.axaml in the <Application.Resources> section so that XAML can see it.)
------------------------------
## Step 4. Configuring the DI container (registration)
For all of this to work, your Views and ViewModels must be created correctly through your IoC container (for example, Microsoft.Extensions.DependencyInjection).
A typical setup in App.axaml.cs:

using System;using Avalonia;using Avalonia.Controls.ApplicationLifetimes;using Avalonia.Markup.Xaml;using AvaloniaApp.ViewModels;using AvaloniaApp.Views;using Microsoft.Extensions.DependencyInjection;
namespace AvaloniaApp;
public class MyDataProcessor : IDataProcessor
{
    public void Process(string input) => Console.WriteLine($"DI service processed: {input}");
}
public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 1. Create the service collection (DI container)
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        Services = serviceCollection.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 2. Resolve the window and view model from the container
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = Services.GetRequiredService<MainViewModel>();
            
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Register business logic
        services.AddSingleton<IDataProcessor, MyDataProcessor>();

        // Register UI layers
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();
    }
}

------------------------------
## Pattern summary

* The UserControl remains a pure UI component with no references to the application architecture.
* Data travels safely up through the UI abstraction layers using standard Avalonia mechanisms (RoutedEvent).
* Behaviors bind the UI event to a ReactiveCommand without cluttering MainWindow.axaml.cs.
* The parent is driven by a full-fledged ViewModel that is easy to cover with unit tests and is created via DI.

To streamline this process and avoid writing converters (CardEventArgsConverter), the UserControl can instead be connected to its parent through two-way property binding (AvaloniaProperty) rather than events.

