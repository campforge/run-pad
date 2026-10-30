using Avalonia;
using Avalonia.Styling;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Desktop;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Windows.Models;
using Moq;
using TextMateSharp.Grammars;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Windows.Models;

// Preferences → Interface → System is built from whatever the OS's desktop integration offers.
public sealed class SystemSettingsPageTests
{
    private static readonly SystemSetting Saved = new()
    {
        Id = "test.saved",
        Title = "Saved",
        Description = "Kept in settings.",
        DefaultValue = true,
        RequiresRestart = true,
    };

    private static readonly SystemSetting Unavailable = new()
    {
        Id = "test.unavailable",
        Title = "Unavailable",
        Description = "Not here.",
        IsAvailable = () => false,
    };

    [Fact]
    public void SystemPage_ShowsTheAvailableSettings_UnderInterface()
    {
        var vm = Create(new FakeIntegration([Saved, Unavailable]), new Dictionary<string, bool>(), out _);

        Assert.Equal(["Saved"], vm.SystemSettings.Select(s => s.Title));
        Assert.Contains(vm.Nodes.Single(n => n.Id == "interface").Children, n => n.Id == "system");
        Assert.EndsWith("Applies after restarting Invoklet.", vm.SystemSettings[0].Description);
    }

    [Fact]
    public void SystemPage_IsLeftOut_WhenNothingApplies()
    {
        var vm = Create(new FakeIntegration([Unavailable]), new Dictionary<string, bool>(), out _);

        Assert.Empty(vm.SystemSettings);
        Assert.Empty(vm.Nodes.Single(n => n.Id == "interface").Children);
    }

    [Fact]
    public void SettingValue_DefaultsAndIsSavedById()
    {
        var saved = new Dictionary<string, bool>();
        var vm = Create(new FakeIntegration([Saved]), saved, out var prefs);
        var setting = vm.SystemSettings[0];

        Assert.True(setting.Value);
        setting.Value = false;

        Assert.False(saved["test.saved"]);
        prefs.Verify(p => p.Save(), Times.Once);
    }

    [Fact]
    public void ExternalSetting_IsReadAndWrittenWhereItLives()
    {
        var stored = false;
        var external = new SystemSetting
        {
            Id = "test.external",
            Title = "External",
            Description = "Elsewhere.",
            External = new ExternalValue(() => stored, v => stored = v),
        };
        var saved = new Dictionary<string, bool>();
        var vm = Create(new FakeIntegration([external]), saved, out _);

        vm.SystemSettings[0].Value = true;

        Assert.True(stored);
        Assert.Empty(saved);
    }

    private static PreferencesWindowViewModel Create(IDesktopIntegration desktop, Dictionary<string, bool> saved,
        out Mock<IPreferencesService> prefs)
    {
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", ThemeName.DarkPlus, true)));
        theme.Setup(t => t.InterfaceThemes).Returns([]);
        theme.Setup(t => t.EditorThemes).Returns([]);
        prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.System).Returns(saved);
        return new PreferencesWindowViewModel(theme.Object, prefs.Object, new LanguageHostRegistry(), desktop);
    }

    private sealed class FakeIntegration(IReadOnlyList<SystemSetting> settings) : IDesktopIntegration
    {
        public IReadOnlyList<SystemSetting> Settings => settings;
        public WindowFrame MainWindowFrame => WindowFrame.CustomTitleBar;
        public AppBuilder ConfigurePlatform(AppBuilder builder, ISystemSettingsReader reader) => builder;
        public void OnApplicationStarted(Application application) { }
    }
}
