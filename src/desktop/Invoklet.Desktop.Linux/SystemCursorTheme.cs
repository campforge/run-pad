using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Invoklet.Desktop.Linux;

/// <summary>
/// Makes Avalonia's Wayland cursors follow the desktop's cursor theme.
/// </summary>
/// <remarks>
/// Avalonia loads Wayland cursors with no theme name, which libwayland-cursor resolves to the
/// "default" theme — usually Adwaita, whatever the desktop is set to. A "default" theme that
/// inherits the desktop's own is put first on the cursor search path before Avalonia starts.
/// </remarks>
internal static class SystemCursorTheme
{
    public static void Apply()
    {
        try
        {
            var theme = DesktopTheme();
            if (string.IsNullOrEmpty(theme)) return;

            var root = Path.Combine(Path.GetTempPath(), $"Invoklet-cursors-{Environment.UserName}");
            var dir = Path.Combine(root, "default");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "index.theme"), $"[Icon Theme]\nInherits={theme}\n");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d
                ? d : Path.Combine(home, ".local", "share");
            var dataDirs = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dd
                ? dd : "/usr/local/share:/usr/share").Split(':');
            var searchPath = Environment.GetEnvironmentVariable("XCURSOR_PATH") is { Length: > 0 } p
                ? p
                : string.Join(':', new[] { Path.Combine(dataHome, "icons"), Path.Combine(home, ".icons") }
                    .Concat(dataDirs.Select(x => Path.Combine(x, "icons")))
                    .Append("/usr/share/pixmaps"));
            // libwayland-cursor reads the native environment, which Environment.SetEnvironmentVariable
            // does not touch on Unix.
            setenv("XCURSOR_PATH", $"{root}:{searchPath}", 1);
        }
        catch (Exception)
        {
            // Cosmetic only: keep the default cursors.
        }
    }

    [DllImport("libc", CharSet = CharSet.Ansi)]
    private static extern int setenv(string name, string value, int overwrite);

    private static string? DesktopTheme()
    {
        if (Environment.GetEnvironmentVariable("XCURSOR_THEME") is { Length: > 0 } fromEnv) return fromEnv;

        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c
            ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var kdeInput = Path.Combine(config, "kcminputrc");
        if (File.Exists(kdeInput))
        {
            var line = File.ReadLines(kdeInput).FirstOrDefault(l => l.StartsWith("cursorTheme=", StringComparison.Ordinal));
            if (line is not null) return line["cursorTheme=".Length..].Trim();
        }

        try
        {
            using var gsettings = Process.Start(new ProcessStartInfo("gsettings", "get org.gnome.desktop.interface cursor-theme")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (gsettings is null) return null;
            var output = gsettings.StandardOutput.ReadToEnd().Trim().Trim('\'');
            gsettings.WaitForExit(2000);
            return gsettings.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
