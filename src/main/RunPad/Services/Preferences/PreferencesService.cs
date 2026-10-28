using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RunPad.Services.Preferences;

public sealed class PreferencesService : IPreferencesService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RunPad", "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly ILogger<PreferencesService> _logger;
    private AppSettings _settings = new();

    public AppearanceSettings Appearance => _settings.Appearance;
    public EditorSettings Editor => _settings.Editor;
    public DebuggerSettings Debugger => _settings.Debugger;
    public CSharpLanguageSettings CSharp => _settings.CSharp;
    public KeyboardSettings Keyboard => _settings.Keyboard;
    public LayoutSettings Layout => _settings.Layout;
    public SessionSettings Session => _settings.Session;
    public ExperimentalSettings Experimental => _settings.Experimental;

    public event Action? Changed;

    public PreferencesService(ILogger<PreferencesService> logger)
    {
        _logger = logger;
        Load();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_settings, SerializerOptions));
        File.Move(tmp, SettingsPath, overwrite: true);
        Changed?.Invoke();
    }

    /// <summary>
    /// The settings as saved, for choices made before Avalonia (and this service) start;
    /// defaults when the file is missing or unreadable.
    /// </summary>
    internal static AppSettings ReadForStartup()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new()
                : new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    private void Load()
    {
        if (!File.Exists(SettingsPath)) return;
        try
        {
            var json = File.ReadAllText(SettingsPath);
            _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            if (_settings.Keyboard.MigrateLegacy()) Save();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Settings file is corrupt; starting with defaults. Path: {Path}", SettingsPath);
        }
    }
}
