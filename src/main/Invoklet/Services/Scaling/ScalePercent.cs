namespace Invoklet.Services.Scaling;

/// <summary>Allowed values of the two size settings, in percent.</summary>
public static class ScalePercent
{
    public const double Default = 100;

    public const double InterfaceMin = 75;
    public const double InterfaceMax = 200;
    public const double CodeZoomMin = 50;
    public const double CodeZoomMax = 300;

    /// <summary>The interface scales offered in Preferences.</summary>
    public static readonly IReadOnlyList<int> InterfacePresets = [90, 100, 110, 125, 150, 175, 200];

    public static double NormalizeInterface(double percent) => Normalize(percent, InterfaceMin, InterfaceMax);

    public static double NormalizeCodeZoom(double percent) => Normalize(percent, CodeZoomMin, CodeZoomMax);

    // A settings file edited by hand, or written by a newer version, may hold anything: a value that
    // isn't a number falls back to 100%, one out of range is brought to the nearest end.
    private static double Normalize(double percent, double min, double max) =>
        double.IsFinite(percent) ? Math.Clamp(Math.Round(percent, 1), min, max) : Default;
}
