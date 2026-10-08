# Avalonia + MVVM + ReactiveUI + Native AOT — Guide

> A comprehensive guide to desktop application architecture with no reflection, no Service Locator, and no memory leaks.
> Stack: **Avalonia 12**, **ReactiveUI**, **Microsoft.Extensions.DependencyInjection**, **Native AOT**, **C# 13**.

---

## Contents

1. [Five principles](#five-principles)
2. [Project setup](#project-setup)
3. [Lifetimes](#lifetimes)
4. [Step-by-step guide](#step-by-step-guide)
   - [Step 1: App.axaml.cs — IoC](#step-1-appaxamlcs--ioc)
   - [Step 2: ViewModel](#step-2-viewmodel)
   - [Step 3: Window / View](#step-3-window--view)
   - [Step 4: UserControl + Attach(vm)](#step-4-usercontrol--attachvm)
   - [Step 5: IWindowService](#step-5-iwindowservice)
   - [Step 6: AXAML bindings](#step-6-axaml-bindings)
   - [Step 7: Subscriptions and Dispose](#step-7-subscriptions-and-dispose)
5. [Patterns — how to do it right](#patterns--how-to-do-it-right)
6. [Anti-patterns — what to avoid](#anti-patterns--what-to-avoid)
7. [Analysis and corrections](#analysis-and-corrections)

---

## Five principles

### 1. Zero-Locator in Views and ViewModels
No `App.ServiceProvider.GetService<T>()` inside windows, UserControls, or ViewModels. All dependencies come only through the DI constructor. A Service Locator is acceptable only in the `WindowService` infrastructure class.

### 2. Compile-time safety
The Native AOT trimmer strips everything that is not called explicitly. All bindings must be compiled (`x:DataType` on every element); a string-based ViewLocator is forbidden; Fody weaving is forbidden.

### 3. Constructor injection + Previewer compatibility
Every Window and UserControl has two constructors: a parameterized one for DI and a parameterless one for the XAML Previewer. The DataContext is set in the parameterized constructor, not via XAML through a service locator.

### 4. Transient for VMs and Windows
A singleton ViewModel = a memory leak and "dirty" state when reopened. All ViewModels and Windows are registered as `Transient`. Singleton is only for infrastructure services.

### 5. The ViewModel owns its state
The View does not write to the ViewModel directly and does not read services on its own. Tooltip texts, computed properties, commands — all of it lives in the ViewModel. The View only binds to ready-made properties and commands.

---

## Project setup

> **Important**: one line in the `.csproj` enables compiled bindings for the whole project. There is no need to write `x:CompileBindings="True"` in every file.

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net9.0</TargetFramework>
  <Nullable>enable</Nullable>

  <!-- Enables compiled bindings globally -->
  <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>

  <!-- Native AOT (optional) -->
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

## Lifetimes

| Lifetime | What to register | Reason |
|---|---|---|
| `AddSingleton` | Infrastructure services: `IPreferencesService`, `IThemeService`, `IWindowService`, loggers | They live for the whole application lifetime. State must persist between window openings. |
| `AddTransient` | All ViewModels, all Windows, all UserControls | Every window opening gets clean state. The GC cleans up the object after it is closed. |
| `AddScoped` | **Do not use** | A scope in a desktop application has no natural boundaries (there are no HTTP requests); it leads to leaks and unpredictable behavior. |

---

## Step-by-step guide

### Step 1: App.axaml.cs — IoC

Initialize the container and register all dependencies. MainWindow is resolved from DI, not created with `new`. The container is not stored in a static field — it is used only to resolve the root object.

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

        // Singleton — infrastructure services
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

### Step 2: ViewModel

The ViewModel inherits from `ReactiveObject`. Properties use `RaiseAndSetIfChanged` or the C# 13 `field` keyword (an AOT-safe alternative to Fody). Asynchronous commands are created with `CreateFromTask`, not fire-and-forget.

```csharp
public sealed class MainWindowViewModel : ReactiveObject
{
    private readonly IWindowService _windowService;

    // The classic way (any C# version)
    private string _title = "RunPad";
    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    // C# 13 field keyword (AOT-safe, no Fody/IL weaving)
    public bool DebugModeEnabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    // Commands: CreateFromTask for async, Create for sync
    public ReactiveCommand<Unit, Unit> OpenPreferencesCommand { get; }
    public ReactiveCommand<Unit, Unit> RunCommand { get; }

    public MainWindowViewModel(IWindowService windowService, IPreferencesService prefs)
    {
        _windowService = windowService;

        // Correct: an async command via CreateFromTask
        OpenPreferencesCommand = ReactiveCommand.CreateFromTask(
            _windowService.ShowPreferencesAsync);

        RunCommand = ReactiveCommand.CreateFromTask(RunAsync);

        // Catch unhandled exceptions from the command
        RunCommand.ThrownExceptions.Subscribe(ex => HandleError(ex));
    }

    private async Task RunAsync() { /* ... */ }
}
```

---

### Step 3: Window / View

A parameterized constructor for the DI runtime + a parameterless constructor for the XAML Previewer. `DataContext` is set only in the parameterized constructor.

```csharp
public sealed partial class MainWindow : Window, IDisposable
{
    private readonly EditorTabsControl _editorTabs;

    // ── DI constructor (runtime) ─────────────────────────────────
    public MainWindow(
        IPreferencesService prefs,
        EditorTabsControl editorTabsCtrl,
        MainWindowViewModel viewModel)
    {
        _editorTabs = editorTabsCtrl;
        DataContext = viewModel;
        InitializeComponent();

        // Mount the UserControl into the ContentControl slot
        this.FindControl<ContentControl>("EditorTabsHost")!.Content = editorTabsCtrl;
        editorTabsCtrl.Attach(viewModel);
    }

    // ── Previewer constructor (designer) ─────────────────────────
    public MainWindow() => InitializeComponent();

    public void Dispose() { _editorTabs.Dispose(); GC.SuppressFinalize(this); }
}
```

```xml
<!-- MainWindow.axaml — commands instead of Click handlers -->
<Window xmlns="https://github.com/avaloniaui"
        xmlns:vm="clr-namespace:MyApp.ViewModels"
        x:DataType="vm:MainWindowViewModel">

  <MenuItem Header="Preferences"
            Command="{Binding OpenPreferencesCommand}"/>

  <!-- The UserControl is mounted via a ContentControl, not directly -->
  <ContentControl x:Name="EditorTabsHost" Grid.Column="1"/>

</Window>
```

---

### Step 4: UserControl + Attach(vm)

The UserControl receives services through its DI constructor and a reference to the ViewModel through an `Attach(vm)` method, which the Window calls after its own initialization. This separates the moment services are connected from the moment data is connected.

```csharp
public partial class EditorTabsControl : UserControl, IDisposable
{
    private IThemeService? _themeService;
    private IDisposable? _activeTabSub;

    // ── Previewer ─────────────────────────────────────────────────
    public EditorTabsControl() => InitializeComponent();

    // ── DI runtime ───────────────────────────────────────────────
    public EditorTabsControl(IThemeService themeService, IPreferencesService prefs)
    {
        InitializeComponent();
        _themeService = themeService;
        _themeService.ThemeChanged += OnThemeChanged;
    }

    // ── Called from the Window after its initialization ──────────
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

### Step 5: IWindowService

The ViewModel calls `IWindowService` without knowing anything about Avalonia. `WindowService` itself uses `IServiceProvider` — the only legitimate place for a Service Locator in this architecture.

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

### Step 6: AXAML bindings

With global CompiledBindings, every root element **and** every `DataTemplate` must declare `x:DataType`. Without it, the compiler does not know the context type.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:models="clr-namespace:MyApp.ViewModels"
             x:DataType="models:MyViewModel">

  <ItemsControl ItemsSource="{Binding Items}">
    <ItemsControl.ItemTemplate>
      <!-- A DataTemplate must also declare x:DataType -->
      <DataTemplate x:DataType="models:ItemViewModel">
        <TextBlock Text="{Binding Name}"/>
      </DataTemplate>
    </ItemsControl.ItemTemplate>
  </ItemsControl>

  <!-- The button is bound to a command, not a Click handler -->
  <Button Command="{Binding OpenSettingsCommand}" Content="Settings"/>

</UserControl>
```

---

### Step 7: Subscriptions and Dispose

Rx subscriptions inside a UserControl are tied to its lifecycle via `WhenActivated` + `DisposeWith`. If the UserControl is mounted dynamically, this is the only way to guarantee unsubscription when it is unmounted.

```csharp
// The WhenActivated pattern (for a UserControl with IViewFor<TViewModel>)
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
// The ViewModel subscribes to the service itself and unsubscribes in Dispose
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
        _prefs.Changed += OnPreferencesChanged; // The ViewModel subscribes itself
    }

    private void OnPreferencesChanged() => SyncTooltips();
    private void SyncTooltips() => TooltipRun = Label("Run", _prefs.Keyboard.Run);

    public void Dispose()
    {
        _prefs.Changed -= OnPreferencesChanged; // Explicit unsubscription
        GC.SuppressFinalize(this);
    }
}
```

---

## Patterns — how to do it right

### ✅ Constructor injection for all dependencies
Windows, UserControls, and ViewModels receive all dependencies through the constructor. The DI container builds the whole graph automatically. No hidden dependencies — everything is visible in the signature.

```csharp
public MainWindow(IPreferencesService prefs, MainWindowViewModel vm) { ... }
```

### ✅ Dual constructor (Previewer compatibility)
A parameterized constructor for DI + a parameterless constructor only for the XAML Previewer. DataContext is set only in the parameterized one.

```csharp
public MyView() => InitializeComponent(); // only InitializeComponent, no logic
```

### ✅ IWindowService for opening windows
The ViewModel calls a service interface without knowing about any Avalonia types. The ViewModel stays testable — the service is easy to mock.

### ✅ ReactiveCommand.CreateFromTask for async
The command automatically manages the `IsExecuting` flag and prevents re-entrant execution.

```csharp
RunCommand = ReactiveCommand.CreateFromTask(RunAsync);
```

### ✅ ThrownExceptions.Subscribe on commands
Unhandled exceptions from a ReactiveCommand terminate the Rx stream and can fail silently.

```csharp
RunCommand.ThrownExceptions.Subscribe(LogError);
```

### ✅ The ViewModel manages tooltip properties itself
Tooltip texts and computed properties are ViewModel state. The ViewModel subscribes to `IPreferencesService.Changed` in its constructor and updates its own properties. The View only binds.

### ✅ Attach(vm) for a UserControl with DI
When a UserControl needs both services (DI) and a ViewModel (passed later), the DI constructor receives the services, and the `Attach(vm)` method is called from the Window after mounting.

### ✅ The C# 13 field keyword instead of Fody
AOT-safe, no IL weaving, no separate backing field.

```csharp
public bool Flag { get; set => this.RaiseAndSetIfChanged(ref field, value); }
```

### ✅ Transient for VMs and Windows
Every window opening creates a new ViewModel instance via DI. On close, the GC removes both objects. No "dirty state" when reopening.

### ✅ x:DataType on every element with a binding
With `AvaloniaUseCompiledBindingsByDefault=true`, every root element and every `DataTemplate` must declare `x:DataType`. Without it, the compiler cannot generate type-safe code.

---

## Anti-patterns — what to avoid

### ❌ Service Locator in a View or ViewModel
`App.ServiceProvider.GetService<T>()` inside a window or VM hides dependencies, makes the class untestable, and violates the dependency inversion principle. Acceptable only in `WindowService`.

### ❌ Reflection-based ViewLocator
```csharp
// DON'T: the AOT trimmer will strip the View classes
var viewName = vm.GetType().FullName.Replace("ViewModel", "View");
```
The application will crash with a `NullReferenceException` at runtime.

### ❌ new MyWindow() or new MyViewModel()
Manually creating objects with dependencies breaks the DI chain. Use the DI container or `IWindowService`.

### ❌ Singleton for ViewModels and Windows
When reopened, the user will see old data and validation errors. A singleton ViewModel keeps the whole data graph in memory forever.

### ❌ ReactiveUI.Fody [Reactive] with Native AOT
Fody modifies IL code after compilation. Native AOT compiles IL into native code before Fody's modification — the result is unpredictable. Use `RaiseAndSetIfChanged` or the C# 13 `field` keyword.

### ❌ Fire-and-forget in a ReactiveCommand
```csharp
// DON'T: IsExecuting does not work, exceptions are lost
ReactiveCommand.Create(() => { _ = AsyncMethod(); });

// Correct:
ReactiveCommand.CreateFromTask(AsyncMethod);
```

### ❌ The View writes to the ViewModel directly
`SyncHotkeyLabels()` in code-behind reads a service and writes to the ViewModel — a violation of the data-flow direction. The ViewModel must update its own state.

### ❌ Click handler instead of a Command
```xml
<!-- DON'T -->
<Button Click="OpenPreferences_Click" Content="Settings"/>

<!-- Correct -->
<Button Command="{Binding OpenPreferencesCommand}" Content="Settings"/>
```

### ❌ Activator.CreateInstance in a factory
```csharp
// DON'T: the AOT trimmer will remove the constructor as "unused"
return (T)Activator.CreateInstance(typeof(T), viewModel)!;
```
Register the type explicitly in DI and resolve it via `GetRequiredService<T>`.

### ❌ Mixing ReactiveUI and CommunityToolkit.Mvvm
`ObservableObject` + `[ObservableProperty]` (CommunityToolkit) and `ReactiveObject` + `WhenAnyValue` (ReactiveUI) are incompatible ecosystems. Pick one and stick with it throughout the project.

---

## Analysis and corrections

A breakdown of specific problems that occur in typical guides on this topic.

### 1. App.ServiceProvider as a static field is an anti-pattern

**Problem:** `public static IServiceProvider ServiceProvider { get; private set; }` — a global access point to the container.

**Solution:** Store `ServiceProvider` as a private instance field of `App`. `WindowService` takes `IServiceProvider` through its constructor — only it is allowed to use it. Everyone else receives dependencies through constructor injection.

---

### 2. ITransientFactory.ViewWithVm uses Activator.CreateInstance

**Problem:** `(T)Activator.CreateInstance(typeof(T), viewModel)!` — the AOT trimmer will remove the constructor, and the application will crash.

**Solution:** A UserControl with a DI constructor should be registered in DI as `Transient` and resolved via `GetRequiredService<T>`. Use the `Attach(vm)` pattern to pass the ViewModel.

---

### 3. ActivatorUtilities.CreateInstance requires annotations for strict AOT

**Problem:** `ActivatorUtilities.CreateInstance<T>` for factories with runtime parameters, without `[DynamicallyAccessedMembers]` or `rd.xml`, produces warnings or fails under strict AOT.

**Solution:** If runtime parameters are needed, pass them through an explicit constructor or a factory method on the service itself, not through `ActivatorUtilities`.

---

### 4. Mixing CommunityToolkit and ReactiveUI in examples

**Problem:** The navigation section uses `ObservableObject` + `[ObservableProperty]` (CommunityToolkit.Mvvm), while the other sections describe ReactiveUI. These are incompatible ecosystems.

**Solution:** For a ReactiveUI project, use `ReactiveObject`, `RaiseAndSetIfChanged`, and `WhenAnyValue` everywhere. Do not use `[ObservableProperty]` from CommunityToolkit.Mvvm.

---

### 5. Per-file x:CompileBindings vs. a global .csproj flag

**Problem:** `x:CompileBindings="True"` in every file is easy to forget; there is no guarantee of consistency.

**Solution:** One line in the `.csproj` — `<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>` — enables compilation for the whole project. The per-file flag is only needed to disable it selectively.

---

### 6. WhenActivated + DisposeWith is missing for UserControls

**Problem:** A manual `Subscribe` without `DisposeWith`. If a UserControl is mounted dynamically, subscriptions accumulate and are never released.

**Solution:** Use `this.WhenActivated(d => { subscription.DisposeWith(d); })`. Avalonia will call dispose when the UserControl is unmounted. Or explicitly implement `IDisposable` with manual unsubscription.

---

### 7. Fire-and-forget in the open-window command example

**Problem:** `ReactiveCommand.Create(() => { windowService.ShowSettingsWindow(); })` — if the method is async, this is fire-and-forget. Exceptions are lost.

**Solution:** `ReactiveCommand.CreateFromTask(() => windowService.ShowSettingsAsync())` with a subscription to `ThrownExceptions`.

---

### 8. A DataTemplate creates the View through the parameterless constructor — a conflict with DI

**Problem:** If a View is registered in DI with a DI constructor but a `DataTemplate` in `App.axaml` triggers its creation, Avalonia uses the parameterless constructor. The View's DI dependencies will not be injected.

**Solution:** For Views used in a DataTemplate, the parameterless constructor must be fully self-sufficient (no DI dependencies), or use the `Attach(vm)` pattern.

---

### 9. The C# 13 field keyword is not mentioned

**Problem:** Fody, CommunityToolkit, and a manual `ref _field` are listed as options for properties. The C# 13 `field` keyword is not mentioned.

**Solution:** The C# 13 `field` keyword is the best option for AOT projects:

```csharp
public bool Flag { get; set => this.RaiseAndSetIfChanged(ref field, value); }
```

No separate backing field, no IL weaving, full AOT compatibility.

---

### 10. ITransientFactory is unnecessary with a proper DI architecture

**Problem:** `ITransientFactory` with `Create<T>()` and `ViewWithVm<T>()` methods is essentially another Service Locator wrapped in an interface, plus the AOT-breaking `Activator.CreateInstance`.

**Solution:** If you need to create a UserControl with a ViewModel, register both as `Transient` and resolve them from DI directly. For a UserControl with complex initialization, use the `Attach(vm)` pattern without a factory.
