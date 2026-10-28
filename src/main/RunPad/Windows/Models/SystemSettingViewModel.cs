using ReactiveUI;
using RunPad.Desktop;
using RunPad.Services.Preferences;

namespace RunPad.Windows.Models;

/// <summary>
/// One check box on Preferences → Interface → System, for a setting the OS's desktop integration
/// offers. Its value is kept in RunPad's settings file, or where the setting itself lives.
/// </summary>
public sealed class SystemSettingViewModel(SystemSetting setting, IPreferencesService prefs) : ReactiveObject
{
    public string Title => setting.Title;

    public string Description => setting.RequiresRestart
        ? setting.Description + " Applies after restarting RunPad."
        : setting.Description;

    public bool Value
    {
        get => setting.External is { } external
            ? external.Read()
            : prefs.System?.GetValueOrDefault(setting.Id, setting.DefaultValue) ?? setting.DefaultValue;
        set
        {
            if (value == Value) return;
            if (setting.External is { } external)
            {
                try
                {
                    external.Write(value);
                }
                catch (Exception)
                {
                    // Left as it was; the check box re-reads the value below.
                }
            }
            else if (prefs.System is { } saved)
            {
                saved[setting.Id] = value;
                prefs.Save();
            }
            this.RaisePropertyChanged();
        }
    }
}
