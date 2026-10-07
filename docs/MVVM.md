In Avalonia 12, using the built-in Microsoft.Extensions.DependencyInjection (MS.DI) container is the most reliable choice. In this version the Avalonia developers removed the classic reflection-based ViewLocator from the standard templates, because it gets in the way of Native AOT compilation and trimming. [1] 
Below is a detailed architecture guide to organizing MVVM + DI in Avalonia 12, with a breakdown of all the critical "pain points".
------------------------------
## Architectural pattern: end-to-end resolution (Zero-Locator)
The best practice for Avalonia 12 is not to use a Service Locator in the View or a ViewLocator. Instead, the whole UI hierarchy is built by composing the ViewModels themselves (injecting child VMs into parent ones), and mapping to Views happens through strongly typed data templates (DataTemplate) in XAML. [1, 2, 3, 4, 5] 
## Step 1: IoC configuration in App.axaml.cs
We initialize the container and build the dependency tree. Note: MainWindow or MainView is resolved from DI, not created with new. [6] 

using System;using Avalonia;using Avalonia.Controls.ApplicationLifetimes;using Avalonia.Markup.Xaml;using Microsoft.Extensions.DependencyInjection;
namespace Avalonia12DI;
public partial class App : Application
{
    // Keep the provider only for root initialization or lazy factories
    public static IServiceProvider ServiceProvider { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);

        ServiceProvider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Resolve the window from DI; it will automatically pull in the required ViewModel
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
        // 1. Services (business logic)
        services.AddSingleton<IApiService, ApiService>();

        // 2. ViewModels (as Transient, so stale state does not accumulate across re-opens)
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();

        // 3. Views
        services.AddTransient<MainWindow>();
        services.AddTransient<MainView>();
    }
}

## Step 2: Connecting the View and ViewModel in code-behind
The View's constructor takes the ViewModel as an argument. Be sure to also keep a parameterless constructor — without it the Previewer (XAML preview) in your IDE will break! [6, 7] 

using Avalonia.Controls;
namespace Avalonia12DI;
public partial class MainWindow : Window
{
    // Called by the DI container at runtime
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    // Called by the Avalonia XAML Previewer in design mode
    public MainWindow()
    {
        InitializeComponent();
    }
}

------------------------------
## Contentious points and "pain points"
## 1. How to solve the XAML Previewer problem?
If a View expects a ViewModel in its constructor, the IDE's XAML previewer fails, because it uses the parameterless constructor and the DataContext stays null. [7] 

* Solution: use Design.DataContext in XAML. This supplies fake data exclusively for the designer, without interfering with DI at runtime.

<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://microsoft.com"
        xmlns:vm="using:Avalonia12DI.ViewModels"
        x:Class="Avalonia12DI.MainWindow"
        x:DataType="vm:MainWindowViewModel">
    
    <!-- ONLY the designer sees this block during development -->
    <Design.DataContext>
        <vm:MainWindowViewModel />
    </Design.DataContext>

    <TextBlock Text="{Binding WelcomeText}" />
</Window>

## 2. How to open new windows or dialogs from a ViewModel?
A ViewModel must not create a new MyWindow() instance directly — this breaks the UI abstraction and breaks unit testing. [7] 

* Solution: the "window service" pattern (IWindowService). We register it as a Singleton, passing it a factory for creating windows.

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
        // Safely resolve the window from the DI container
        var settingsWindow = _serviceProvider.GetRequiredService<SettingsWindow>();
        settingsWindow.Show();
    }
}

Now any ViewModel only needs IWindowService injected through its constructor and can call ShowSettings().
## 3. Navigation: how to switch Views/pages on screen?
Instead of custom locators, in Avalonia 12 it is better to use strongly generated DataTemplates in App.axaml.
If the parent ViewModel holds the current child ViewModel in a CurrentPage property, Avalonia figures out by itself which View to render. [2] 

* In XAML (App.axaml):

<Application.DataTemplates>
    <!-- If the DataContext is MainViewModel, render MainView -->
    <DataTemplate DataType="using:Avalonia12DI.ViewModels.MainViewModel">
        <views:MainView />
    </DataTemplate>
    
    <!-- If the DataContext is SettingsViewModel, render SettingsView -->
    <DataTemplate DataType="using:Avalonia12DI.ViewModels.SettingsViewModel">
        <views:SettingsView />
    </DataTemplate>
</Application.DataTemplates>


* In the ViewModel:

public partial class MainNavigationViewModel : ObservableObject
{
    [ObservableProperty]
    private object _currentPage; // The required ViewModel goes here

    public MainNavigationViewModel(MainViewModel mainPage)
    {
        _currentPage = mainPage; // The main page is open initially
    }

    public void GoToSettings(SettingsViewModel settingsPage)
    {
        CurrentPage = settingsPage; // Avalonia will switch the View on screen automatically!
    }
}

## 4. Dynamic parameters in a ViewModel (the factory pattern)
What if UserDetailViewModel requires a user ID (int userId) that only becomes known while the program runs, but it also needs the IApiService singleton from DI?

* Solution: use a factory delegate or an explicit factory class. Microsoft.Extensions.DependencyInjection does not support factories with runtime parameters out of the box, so a custom factory is written:

public class UserDetailViewModel : ObservableObject
{
    private readonly IApiService _apiService;
    public int UserId { get; }

    // The constructor takes both services and a runtime parameter
    public UserDetailViewModel(IApiService apiService, int userId)
    {
        _apiService = apiService;
        UserId = userId;
    }
}
// Factory that controls creation
public interface IUserDetailFactory
{
    UserDetailViewModel Create(int userId);
}
public class UserDetailFactory : IUserDetailFactory
{
    private readonly IServiceProvider _serviceProvider;

    public UserDetailFactory(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

    public UserDetailViewModel Create(int userId)
    {
        // ActivatorUtilities automatically takes IApiService from the container, 
        // and passes userId into the constructor manually!
        return ActivatorUtilities.CreateInstance<UserDetailViewModel>(_serviceProvider, userId);
    }
}

DI registration: services.AddSingleton<IUserDetailFactory, UserDetailFactory>();. [7] 
------------------------------
## Lifetimes checklist for Avalonia
When registering in MS.DI, it is important to follow the rules strictly to avoid memory leaks:

* AddSingleton: your infrastructure services (ApiService, DatabaseConfiguration, WindowService). They live for the entire application run. [7] 
* AddTransient: all ViewModels and Views. When the user closes a page/window, these objects must be collected by the garbage collector (GC). If you make them singletons, old data will hang in memory forever. [7] 
* AddScoped: do not use in Avalonia desktop applications. A scope on the desktop has no clear boundaries (unlike HTTP requests on the web); using it leads to bugs and confusion.

Possible follow-up topics:

* Is Native AOT compilation planned?
* Is an example of ReactiveUI integration needed (Avalonia 12 syntax has its own specifics)?
* What is the application's architecture: single-page (SingleView) or multi-window (Desktop)?


[1] [https://github.com](https://github.com/AvaloniaUI/avalonia-docs/issues/525)
[2] [https://docs.avaloniaui.net](https://docs.avaloniaui.net/docs/data-templates/view-locator)
[3] [https://github.com](https://github.com/AvaloniaUI/Avalonia/discussions/15630)
[4] [https://github.com](https://github.com/AvaloniaUI/Avalonia/discussions/17576)
[5] [https://v11.docs.avaloniaui.net](https://v11.docs.avaloniaui.net/docs/concepts/view-locator)
[6] [https://github.com](https://github.com/AvaloniaUI/Avalonia/discussions/18039)
[7] [https://docs.avaloniaui.net](https://docs.avaloniaui.net/docs/app-development/dependency-injection)


Using Native AOT and ReactiveUI in Avalonia 12 imposes very strict constraints on the architecture. Native AOT completely forbids dynamic code generation (Reflection.Emit), which ReactiveUI used to rely on for bindings.
So in Avalonia 12 the main rule becomes compile-time safety. We completely abandon reflection in favor of source generators and strongly typed XAML.
Below is a comprehensive guide to organizing code for a scenario with many windows and UserControls.
------------------------------
## Architectural manifesto for Native AOT

   1. No reflection when looking up Views: the old ViewLocator, which found Views by class name via strings, is incompatible with AOT. A strongly typed DataTemplate in XAML is used instead.
   2. CompiledBindings are strictly required: in every .axaml file the root element must contain x:CompileBindings="True". Without it, a Native AOT build either fails or the application crashes at runtime.
   3. Source generators instead of reflection: should properties use the [Reactive] attributes from the ReactiveUI.Fody package? No! Fody rewrites IL and often conflicts with AOT. Nowadays, for ReactiveUI and AOT, either the CommunityToolkit.Mvvm code generator (the [ObservableProperty] attribute) is used, or properties are written by hand following ReactiveUI conventions, or the official ReactiveUI source generator is used (depending on the chosen packages). The most AOT-safe manual/generator approach is shown below.

------------------------------
## 1. How to write a ViewModel (ReactiveUI + AOT style)
To make sure properties work under Native AOT, write them via a generator or the standard RaiseAndSetIfChanged. Using commands via ReactiveCommand is completely safe.

using System;using System.Reactive;using ReactiveUI;
namespace AvaloniaAotApp.ViewModels;
// Register every ViewModel in DI as Transient!
public class MainViewModel : ReactiveObject
{
    private string _title = "Home page";
    private readonly ITransientFactory _factory;

    // 1. Properties are written the classic ReactiveUI way (AOT-safe)
    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    // Child ViewModels that will be displayed as UserControls
    private ReactiveObject? _currentContent;
    public ReactiveObject? CurrentContent
    {
        get => _currentContent;
        set => this.RaiseAndSetIfChanged(ref _currentContent, value);
    }

    // 2. ReactiveUI commands
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }

    // 3. The constructor takes dependencies from DI
    public MainViewModel(IWindowService windowService, ITransientFactory factory)
    {
        _factory = factory;
        
        // Switch to a child UserControl (created via the factory)
        CurrentContent = _factory.Create<DashboardViewModel>();

        // Command that opens a new window via the window service
        OpenSettingsCommand = ReactiveCommand.Create(() => 
        {
            windowService.ShowSettingsWindow();
        });
    }
}

------------------------------
## 2. How to write a View (.axaml and .axaml.cs)
Avalonia 12 + AOT uses constructor injection for Views. But so as not to break the Previewer (designer), we use a special trick.
## The .axaml (using a UserControl as an example):
Note x:CompileBindings="True" and x:DataType.

<UserControl xmlns="https://github.com"
             xmlns:x="http://microsoft.com"
             xmlns:vm="using:AvaloniaAotApp.ViewModels"
             x:Class="AvaloniaAotApp.Views.DashboardView"
             x:DataType="vm:DashboardViewModel"
             x:CompileBindings="True">
	
    <!-- The previewer picks up this DataContext during development -->
    <Design.DataContext>
        <!-- For AOT the ViewModel needs a parameterless constructor here, or a mock -->
        <vm:DashboardViewModel />
    </Design.DataContext>

    <StackPanel Spacing="10" Margin="20">
        <TextBlock Text="This is a child UserControl (Dashboard)" Classes="h1"/>
        <TextBlock Text="{Binding StatusText}" />
    </StackPanel>
</UserControl>

## The .axaml.cs (code-behind):
Use a strongly typed DataContext.

using Avalonia.Controls;using AvaloniaAotApp.ViewModels;
namespace AvaloniaAotApp.Views;
public partial class DashboardView : UserControl
{
    // This constructor is called by the DI container at runtime
    public DashboardView(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    // This constructor STAYS EMPTY for the Avalonia XAML Previewer
    public DashboardView()
    {
        InitializeComponent();
    }
}

------------------------------
## 3. Solving the "many windows and UserControls" problem with DI
Since the reflection-based ViewLocator is banned by the AOT compiler, we use two binding mechanisms.
## Mechanism A: for dynamic UserControls within one window
Use strongly typed DataTemplates in App.axaml. When Avalonia sees that the CurrentContent property in the ViewModel changed to DashboardViewModel, it does not look up a class by string but substitutes DashboardView from DI in a hard-compiled way.
In App.axaml:

<Application.DataTemplates>
    <!-- Direct, hard mapping without reflection -->
    <DataTemplate DataType="using:AvaloniaAotApp.ViewModels.DashboardViewModel">
        <!-- Important: do we need a factory method in XAML for the View to be created from DI? 
             No, XAML will call the View's parameterless constructor, BUT we will override that 
             by initializing child elements through the tree. The best way for AOT is below: -->
    </DataTemplate>
</Application.DataTemplates>

Wait — if XAML calls the View's parameterless constructor, the View will not receive its ViewModel through the DI constructor!
How to solve this correctly for AOT and DI:
Instead of global DataTemplates that call the parameterless constructor, inject child Views explicitly into the parent View via a factory, or use a ContentControl container with the following approach:

// In the parent View (for example, MainView.axaml.cs)
public partial class MainView : UserControl
{
    private readonly ITransientFactory _factory;

    public MainView(MainViewModel viewModel, ITransientFactory factory)
    {
        InitializeComponent();
        DataContext = viewModel;
        _factory = factory;

        // Watch for CurrentContent changes in the ViewModel (ReactiveUI style)
        viewModel.WhenAnyValue(x => x.CurrentContent)
                 .Subscribe(vm => 
                 {
                     if (vm is DashboardViewModel dashboardVm)
                     {
                         // Resolve the View from DI, passing the ready VM
                         var view = _factory.Create<DashboardView>(dashboardVm);
                         ContentControlElement.Content = view;
                     }
                 });
    }

    public MainView() => InitializeComponent();
}

## Mechanism B: for opening new Windows
Create an IWindowService interface and an ITransientFactory factory. They are registered as Singletons.

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
        // Resolve the window from DI. The container will create SettingsWindow itself, 
        // inject SettingsViewModel into it, and the required services into that.
        var window = _serviceProvider.GetRequiredService<SettingsWindow>();
        window.Show();
    }
}

A helper factory class for dynamic resolution (replacing ActivatorUtilities for better AOT support):

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
        // When a VM needs to be passed into the View manually
        return (T)Activator.CreateInstance(typeof(T), viewModel)!;
    }
}

Registration in App.axaml.cs:

private void ConfigureServices(IServiceCollection services)
{
    services.AddSingleton<IWindowService, WindowService>();
    services.AddSingleton<ITransientFactory, TransientFactory>();

    // Windows
    services.AddTransient<MainWindow>();
    services.AddTransient<SettingsWindow>();

    // Controls (UserControls)
    services.AddTransient<DashboardView>();
    services.AddTransient<ProfileView>();

    // ViewModels
    services.AddTransient<MainWindowViewModel>();
    services.AddTransient<SettingsViewModel>();
    services.AddTransient<DashboardViewModel>();
    services.AddTransient<ProfileViewModel>();
}

------------------------------
## 4. Principles for extracting ViewModels and splitting into UserControls
Avalonia desktop applications easily turn into "spaghetti" if the interface is not broken up in time. Follow these rules:
## When to extract a separate ViewModel?

   1. Single Responsibility Principle (SRP): if a screen solves two different tasks (for example, a list of users and detailed editing of the selected user), those are two different ViewModels (UserListViewModel and UserEditViewModel).
   2. Context reuse: if a block of data (for example, a product card or a weather widget) appears in different parts of the application, it must have its own isolated WidgetViewModel.
   3. Data lifecycle: if the data must be fully cleared/reset when a particular tab or panel is closed, extract that panel into a separate Transient ViewModel. When the View is destroyed, the VM is destroyed too.

## When should a single AXAML be split into UserControls?

   1. The 300-line rule: if your .axaml file exceeds 300-400 lines of markup, it is physically hard to read. Move logical blocks (side menu, header, data table) into separate UserControls.
   2. Complex display states (visual isolation): if a page contains an area that changes significantly depending on state (for example: Loading -> Error -> Showing data), that area is better turned into a ContentControl into which different UserControls are swapped.
   3. Isolating ReactiveUI subscriptions: if a View has a lot of logic with complex Rx chains (this.WhenAnyValue(...)), turning that piece into a UserControl isolates those subscriptions. This prevents memory leaks, because the subscriptions are disposed together with the lifecycle of that small UserControl.

## Working summary

* Every UserControl gets its own .axaml.cs file with two constructors (one with a parameter for DI and an empty one for the previewer).
* All screen switches happen by replacing the CurrentContent property in the parent ViewModel, and the View itself is created by the ITransientFactory factory.
* All opening of new Windows is encapsulated in WindowService.

If specific linker errors (Trimming/AOT warnings) come up when compiling ReactiveUI commands, an rd.xml file (Runtime Directives) can be configured to suppress those warnings.
Further architecture questions worth covering:

* Modal dialogs that return a result (for example, ShowDialog<TResult>).
* Passing dynamic runtime parameters deep into child UserControls when they are created.

Based on this analysis of the Avalonia 12 + ReactiveUI + Native AOT architecture, the approaches can be clearly divided into recommended ones (patterns) and dangerous ones (anti-patterns).
Since Native AOT completely removes dynamic code generation at runtime, classic techniques from WPF/older Avalonia become critical errors here.
------------------------------
## 🏛️ Architectural patterns (how to do it RIGHT)

   1. Constructor injection for Views
   * Essence: pass the ViewModel directly as a View constructor parameter, View(MyViewModel vm). The IoC container resolves all dependencies down the chain by itself.
      * Benefit: full compile-time checking, no hidden dependencies.
   2. Strict binding compilation (CompiledBindings)
   * Essence: mandatory use of the x:CompileBindings="True" and x:DataType="..." attributes in every XAML file.
      * Benefit: errors in property paths are caught at build time, and the Native AOT compiler generates the fastest possible direct C# code instead of slow reflection.
   3. The "window service" pattern (IWindowService)
   * Essence: move the logic for creating, configuring, and calling .Show() or .ShowDialog() for new windows into an external Singleton service.
      * Benefit: separation of concerns. ViewModels stay free of UI code and can be covered by unit tests by replacing this service with a mock.
   4. Dual constructor in the View (the Previewer compatibility pattern)
   * Essence: a parameterized constructor for DI at runtime plus a parameterless constructor public MyView() { InitializeComponent(); } exclusively for the IDE designer.
      * Benefit: the XAML designer works reliably, and the application keeps its architectural cleanliness at runtime.
   5. ViewModel composition (hierarchical MVVM)
   * Essence: the main ViewModel (MainViewModel) manages the lifecycle and holds references to child Transient ViewModels (CurrentPage = dashboardViewModel). Changing screens is simply replacing the object in a property.
      * Benefit: fits ReactiveUI's reactive manifesto perfectly and is easy to track via this.WhenAnyValue(...).
   
------------------------------
## ⚠️ Anti-patterns (what to AVOID)

   1. Using a Service Locator (App.ServiceProvider.GetService<T>()) inside the UI or a VM
   * Why it is harmful: using a global access point to the container right inside the logic hides the class's dependencies. The code becomes fragile and impossible to test in isolation. The container should be called only once, at the very top level (in App.axaml.cs at startup).
   2. A reflection-based ViewLocator (dynamic View lookup by strings)
   * Why it is harmful: the classic approach var viewName = vm.GetType().FullName.Replace("ViewModel", "View") is guaranteed to break Native AOT. During the build the trimmer will strip the View classes, deciding they are unused, and the application will crash at runtime with a NullReferenceException.
   3. Using the new operator to create Views/ViewModels with dependencies
   * Why it is harmful: if you write var vm = new SettingsViewModel();, all the services this ViewModel requires (database, API, logging) have to be threaded manually through the whole chain of parent classes, or a Service Locator has to be used. The whole point of DI is lost.
   4. Registering windows and ViewModels as Singletons
   * Why it is harmful: if SettingsViewModel is registered as a singleton, then when the settings window is reopened the user will see old, un-reset data and previous validation errors. Windows and ViewModels must have a Transient lifetime (created anew on every request) to avoid memory leaks and state bugs.
   5. Using ReactiveUI.Fody ([Reactive] properties) with Native AOT
   * Why it is harmful: Fody modifies IL code (weaving) after compilation. This often leads to unpredictable behavior of the Native AOT linker. For properties in AOT scenarios it is safer to use the classic RaiseAndSetIfChanged or the source generator from CommunityToolkit.Mvvm.
   6. Mixing contexts (not splitting into UserControls)
   * Why it is harmful: trying to fit a complex screen with many features into one massive 1000+ line .axaml file causes ReactiveUI subscriptions (WhenAnyValue) to start overlapping, and the garbage collector (GC) cannot free memory because small components are held tightly by references from the giant parent View.


