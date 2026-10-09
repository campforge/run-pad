In Avalonia UI a TemplatedControl is fundamentally different from a UserControl. Whereas a UserControl tightly couples markup and logic, a TemplatedControl separates them: all logic is written in a C# class, and the appearance is moved entirely into a XAML template (usually an .axaml file in a Themes or Styles folder).
This is the ideal approach for building reusable component libraries (design systems) that need to support ReactiveUI, DI, and flexible customization.
Below is a detailed architecture guide to creating a TemplatedControl with custom events, ReactiveUI integration, and DI.
------------------------------
## Guide: routed events in a TemplatedControl (ReactiveUI + DI)
## The main architectural difference
A TemplatedControl has no direct code-behind for its markup. Instead, the control overrides the OnApplyTemplate() method. In that method it finds the elements inside its template (for example, buttons), subscribes to their local actions, and forwards them outward as a RoutedEvent.
------------------------------
## Step 1. Creating the control's C# class
Create a custom control, CustomCard.cs. It will contain the event registration and the logic for grabbing elements from the template.
File: CustomCard.cs

using System;using Avalonia;using Avalonia.Controls;using Avalonia.Controls.Primitives;using Avalonia.Interactivity;
namespace AvaloniaApp.Controls;
// Class for passing data out of the TemplatedControl
public class CardSubmittedEventArgs : RoutedEventArgs
{
public string Value { get; }
public CardSubmittedEventArgs(RoutedEvent routedEvent, string value) : base(routedEvent)
{
Value = value;
}
}
public class CustomCard : TemplatedControl
{
// 1. Register a Bubble event that will bubble up the UI tree
public static readonly RoutedEvent<CardSubmittedEventArgs> SubmittedEvent =
RoutedEvent.Register<CustomCard, CardSubmittedEventArgs>(
nameof(Submitted),
RoutingStrategies.Bubble);

    public event EventHandler<CardSubmittedEventArgs>? Submitted
    {
        add => AddHandler(SubmittedEvent, value);
        remove => RemoveHandler(SubmittedEvent, value);
    }

    // References to the template elements needed by the logic
    private Button? _submitButton;
    private TextBox? _inputTextBox;

    // 2. Called when Avalonia has applied the XAML template to this control
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        // Unsubscribe from old elements if the template is re-applied (to prevent memory leaks)
        if (_submitButton != null)
            _submitButton.Click -= OnSubmitButtonClick;

        // Find the required elements inside the template by name (PART_...)
        _submitButton = e.NameScope.Find<Button>("PART_SubmitButton");
        _inputTextBox = e.NameScope.Find<TextBox>("PART_InputTextBox");

        // Subscribe to the inner button's click event
        if (_submitButton != null)
            _submitButton.Click += OnSubmitButtonClick;
    }

    private void OnSubmitButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_inputTextBox == null) return;

        string currentText = _inputTextBox.Text ?? string.Empty;

        // 3. Raise our custom event outward
        var args = new CardSubmittedEventArgs(SubmittedEvent, currentText);
        RaiseEvent(args);
    }
}

Naming note: in a TemplatedControl, elements inside the template are conventionally named with the PART_ prefix (for example, PART_SubmitButton). This signals to developers that these elements are critical for the class's logic to work.

------------------------------
## Step 2. Defining the XAML template (style)
Now describe what our CustomCard should look like. Create a styles file.
File: CustomCardStyle.axaml

<Styles xmlns="https://github.com"
xmlns:x="http://microsoft.com"
xmlns:controls="using:AvaloniaApp.Controls">

    <!-- Target the style at our custom class -->
    <Style Selector="controls|CustomCard">
        <!-- Set default properties in case they are not specified explicitly -->
        <Setter Property="Background" Value="#F5F5F5"/>
        <Setter Property="BorderBrush" Value="#E0E0E0"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Padding" Value="15"/>

        <!-- Define the template itself -->
        <Setter Property="Template">
            <ControlTemplate>
                <Border Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}"
                        Padding="{TemplateBinding Padding}"
                        CornerRadius="8">

                    <StackPanel Spacing="10">
                        <TextBlock Text="Component: TemplatedControl" FontWeight="Bold"/>

                        <!-- Names must exactly match what we look up in OnApplyTemplate -->
                        <TextBox x:Name="PART_InputTextBox" Watermark="Enter text..." />
                        <Button x:Name="PART_SubmitButton" Content="Confirm" HorizontalAlignment="Right"/>
                    </StackPanel>

                </Border>
            </ControlTemplate>
        </Setter>
    </Style>
</Styles>

For the project to see this style, register it in App.axaml:

<Application.Styles>
<FluentTheme />
<!-- Include our control's style file -->
<StyleInclude Source="using:AvaloniaApp.Controls/CustomCardStyle.axaml" />
</Application.Styles>

------------------------------
## Step 3. Creating the parent ViewModel and service (DI + ReactiveUI)
The ViewModel here is exactly the same as the one used for the UserControl. It does not care which kind of element raised the event. It works only with plain data and depends on services via DI.

using System;using System.Reactive;using ReactiveUI;
namespace AvaloniaApp.ViewModels;
public interface INotificationService { void Send(string message); }
public class MainViewModel : ViewModelBase
{
private readonly INotificationService _notificationService;
private string _logText = "No messages";

    public string LogText
    {
        get => _logText;
        set => this.RaiseAndSetIfChanged(ref _logText, value);
    }

    // ReactiveUI command for processing the data
    public ReactiveCommand<string, Unit> ProcessTextCommand { get; }

    // Constructor into which the DI container passes the required service
    public MainViewModel(INotificationService notificationService)
    {
        _notificationService = notificationService;

        ProcessTextCommand = ReactiveCommand.Create<string>(text =>
        {
            // Call the external service's logic
            _notificationService.Send(text);

            // Update the UI state in the ViewModel
            LogText = $"[DI Service]: Data '{text}' sent successfully!";
        });
    }
}

------------------------------
## Step 4. Using it in the parent View (XAML)
In the parent window we embed CustomCard and use Avalonia.Xaml.Behaviors to bind the bubbling Submitted event directly to ProcessTextCommand.
File: MainWindow.axaml

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

    <!-- Register the argument converter in the window resources (or in App.axaml) -->
    <Window.Resources>
        <converters:CardSubmittedConverter x:Key="CardSubmittedConverter"/>
    </Window.Resources>

    <StackPanel Margin="20" Spacing="15" VerticalAlignment="Center">

        <TextBlock Text="{Binding LogText}" Foreground="DarkBlue" FontSize="14" HorizontalAlignment="Center"/>

        <!-- Use our TemplatedControl -->
        <controls:CustomCard Background="#FFF9E7" BorderBrush="Orange">
            <i:Interaction.Behaviors>
                <!-- Listen for the event declared in the CustomCard.cs C# class -->
                <ia:EventTriggerBehavior EventName="Submitted">
                    <!-- Invoke the ReactiveCommand from MainViewModel -->
                    <ia:InvokeCommandAction
                        Command="{Binding ProcessTextCommand}"
                        InputConverter="{StaticResource CardSubmittedConverter}"/>
                </ia:EventTriggerBehavior>
            </i:Interaction.Behaviors>
        </controls:CustomCard>

    </StackPanel>
</Window>

## Event argument converter
A simple converter extracts the Value property from CardSubmittedEventArgs and passes it to the command as a string:

using System;using System.Globalization;using Avalonia.Data.Converters;using AvaloniaApp.Controls;
namespace AvaloniaApp.Converters;
public class CardSubmittedConverter : IValueConverter
{
public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
{
if (value is CardSubmittedEventArgs args)
{
return args.Value; // Return a plain string for ReactiveCommand<string, Unit>
}
return null;
}

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

------------------------------
## Step 5. Configuring the DI container
When the application initializes, register all dependencies, including services, windows, and view models, in App.axaml.cs.

using System;using Avalonia;using Avalonia.Controls.ApplicationLifetimes;using Avalonia.Markup.Xaml;using AvaloniaApp.ViewModels;using AvaloniaApp.Views;using Microsoft.Extensions.DependencyInjection;
namespace AvaloniaApp;
public class ConsoleNotificationService : INotificationService
{
public void Send(string message) => Console.WriteLine($"[DI Backend] Log: {message}");
}
public partial class App : Application
{
public IServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var serviceCollection = new ServiceCollection();

        // Register the notification service as a singleton
        serviceCollection.AddSingleton<INotificationService, ConsoleNotificationService>();

        // Register the UI
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
## Architectural takeaways

* Full decoupling: the TemplatedControl's logic lives in the .cs file and its markup in the .axaml styles file. The appearance can be rewritten entirely (colors, structure, animations) without breaking the event and data-submission logic.
* Clean MVVM: the window knows the control exists, the control knows its inner elements, but none of them know anything about a specific ViewModel or DI services.
* Ready for reuse: such a control can be moved into a separate class library (NuGet package) and used across many different projects.


