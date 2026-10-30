using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;

namespace Invoklet.Services.Scaling;

/// <summary>
/// The two size multipliers of the whole application, kept in the preferences: the interface scale
/// (every window, code included) and the code zoom on top of it (every code editor). One instance per
/// preferences object, so all windows and tabs of the app share it. Code is shown at
/// <c>EditorSettings.FontSize × InterfaceScale × CodeZoom</c> logical units: the interface scale
/// comes from the window's layout transform, the code zoom from the editor's font size. The display's
/// own pixel density is Avalonia's business and is never part of either.
/// </summary>
public sealed class ViewScale : ICodeZoom
{
    private static readonly ConditionalWeakTable<IPreferencesService, ViewScale> Instances = new();

    // Wheel ticks and repeated Zoom In come in bursts; the file is written once a burst is over.
    internal static readonly TimeSpan DefaultSaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly IPreferencesService _prefs;
    private readonly TimeSpan _saveDelay;
    private DispatcherTimer? _saveTimer;
    private double _interfacePercent;
    private double _codeZoomPercent;

    private ViewScale(IPreferencesService prefs, TimeSpan saveDelay)
    {
        _prefs = prefs;
        _saveDelay = saveDelay;
        _interfacePercent = ReadInterfacePercent();
        _codeZoomPercent = ReadCodeZoomPercent();
        // Preferences edited elsewhere (the Preferences window) reach every window through here.
        prefs.Changed += Sync;
    }

    /// <param name="saveDelay">How long after the last change the settings are written; only the first call for a set of preferences decides it.</param>
    public static ViewScale For(IPreferencesService prefs, TimeSpan? saveDelay = null)
        => Instances.GetValue(prefs, p => new ViewScale(p, saveDelay ?? DefaultSaveDelay));

    /// <summary>Raised on the UI thread after either multiplier changed.</summary>
    public event Action? Changed;

    public double InterfaceScale => _interfacePercent / 100;
    public double CodeZoom => _codeZoomPercent / 100;
    public int InterfaceScalePercent => (int)Math.Round(_interfacePercent);
    public int CodeZoomPercent => (int)Math.Round(_codeZoomPercent);

    /// <summary>True while a change waits for its delayed write to the settings file.</summary>
    internal bool HasPendingSave => _saveTimer is { IsEnabled: true };

    public void SetInterfaceScalePercent(double percent)
    {
        percent = ScalePercent.NormalizeInterface(percent);
        if (percent == _interfacePercent) return;
        _interfacePercent = percent;
        if (_prefs.Appearance is { } appearance) appearance.InterfaceScalePercent = percent;
        RaiseAndSave();
    }

    public void ResetInterfaceScale() => SetInterfaceScalePercent(ScalePercent.Default);

    public void SetCodeZoomPercent(double percent)
    {
        percent = ScalePercent.NormalizeCodeZoom(percent);
        if (percent == _codeZoomPercent) return;
        _codeZoomPercent = percent;
        if (_prefs.Editor is { } editor) editor.ZoomPercent = percent;
        RaiseAndSave();
    }

    public void ZoomIn() => SetCodeZoomPercent(EditorZoom.NextStep(CodeZoomPercent));

    public void ZoomOut() => SetCodeZoomPercent(EditorZoom.PreviousStep(CodeZoomPercent));

    public void ResetCodeZoom() => SetCodeZoomPercent(ScalePercent.Default);

    /// <summary>Writes a pending change now (on exit, so the last wheel tick isn't lost).</summary>
    public void Flush()
    {
        if (!HasPendingSave) return;
        _saveTimer!.Stop();
        _prefs.Save();
    }

    private void RaiseAndSave()
    {
        Changed?.Invoke();
        _saveTimer ??= CreateSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private DispatcherTimer CreateSaveTimer()
    {
        var timer = new DispatcherTimer { Interval = _saveDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _prefs.Save();
        };
        return timer;
    }

    // Saving raises Changed too; the values then match and nothing is raised again.
    private void Sync()
    {
        var interfacePercent = ReadInterfacePercent();
        var codeZoomPercent = ReadCodeZoomPercent();
        if (interfacePercent == _interfacePercent && codeZoomPercent == _codeZoomPercent) return;
        _interfacePercent = interfacePercent;
        _codeZoomPercent = codeZoomPercent;
        Changed?.Invoke();
    }

    private double ReadInterfacePercent() =>
        ScalePercent.NormalizeInterface(_prefs.Appearance?.InterfaceScalePercent ?? ScalePercent.Default);

    private double ReadCodeZoomPercent() =>
        ScalePercent.NormalizeCodeZoom(_prefs.Editor?.ZoomPercent ?? ScalePercent.Default);
}
