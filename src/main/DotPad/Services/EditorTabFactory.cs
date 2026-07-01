using Avalonia;
using Avalonia.Media;
using DotPad.Contracts;
using DotPad.Contracts.IntelliSense;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.IntelliSense;
using DotPad.Services.Preferences;
using DotPad.ViewModels;
using DotPad.Views;
using Microsoft.Extensions.Logging;

namespace DotPad.Services;

internal sealed class EditorTabFactory(
    IThemeService themeService,
    ILanguageHostRegistry registry,
    IPreferencesService prefs,
    ILoggerFactory loggerFactory) : IEditorTabFactory
{
    private IIntelliSenseService? _intelliSense;

    public EditorTabControl Create(EditorTabViewModel vm)
    {
        _intelliSense ??= new CSharpIntelliSenseService(
            new CSharpReferenceService().GetDefault(),
            loggerFactory.CreateLogger<CSharpIntelliSenseService>());

        return new EditorTabControl(
            vm, themeService, registry, prefs, _intelliSense,
            GetThemeBrush("DotPad.Editor.BackgroundBrush"));
    }

    private static IBrush? GetThemeBrush(string key)
    {
        object? val = null;
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out val);
        return val as IBrush;
    }
}
