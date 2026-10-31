using Avalonia.Styling;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Windows.Models;
using Moq;
using TextMateSharp.Grammars;

namespace Invoklet.NonUI.Tests.App.Windows.Models.Fixtures;

internal sealed class PreferencesWindowViewModelLoggingFixture
{
    public LoggingSettings Logging { get; } = new();

    public Mock<IPreferencesService> Prefs { get; } = new();

    public PreferencesWindowViewModel CreateSut()
    {
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", ThemeName.DarkPlus, true)));
        theme.Setup(t => t.InterfaceThemes).Returns([]);
        theme.Setup(t => t.EditorThemes).Returns([]);
        Prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        Prefs.Setup(p => p.Logging).Returns(Logging);
        return new PreferencesWindowViewModel(theme.Object, Prefs.Object, new LanguageHostRegistry());
    }
}
