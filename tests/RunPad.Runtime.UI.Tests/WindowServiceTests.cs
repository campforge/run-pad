using Avalonia.Headless.XUnit;
using Moq;
using RunPad.Services;
using RunPad.Windows;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #100 — WindowService used to take the whole IServiceProvider and resolve
// DependenciesWindow/PreferencesWindow/IThemeService inside its methods (Service Locator), which
// meant testing it in isolation needed a full ServiceCollection/ServiceProvider just to exercise one
// method. Now it takes explicit Func<TWindow> factories plus IThemeService directly — these tests are
// the payoff: no DI container anywhere, just plain fakes.
public class WindowServiceTests
{
    [AvaloniaFact]
    public async Task ShowDependenciesAsync_Should_CreateItsWindow_ViaTheInjectedFactory()
    {
        var window = new DependenciesWindow();
        var factoryCalled = false;
        var service = new WindowService(
            dependenciesWindowFactory: () => { factoryCalled = true; return window; },
            preferencesWindowFactory: () => throw new InvalidOperationException("not expected here"),
            themeService: Mock.Of<IThemeService>());

        await service.ShowDependenciesAsync();

        Assert.True(factoryCalled);
    }

    [AvaloniaFact]
    public async Task ShowPreferencesAsync_Should_CreateItsWindow_ViaTheInjectedFactory()
    {
        var window = new PreferencesWindow();
        var factoryCalled = false;
        var service = new WindowService(
            dependenciesWindowFactory: () => throw new InvalidOperationException("not expected here"),
            preferencesWindowFactory: () => { factoryCalled = true; return window; },
            themeService: Mock.Of<IThemeService>());

        await service.ShowPreferencesAsync();

        Assert.True(factoryCalled);
    }
}
